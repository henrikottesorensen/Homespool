using System;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Host.Services;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="SmtpConnectivityProbe"/> against <see cref="FakeSmtpTransport"/> - same reasoning as
/// <see cref="SmtpEmailSenderTests"/>: MailKit's own correctness is trusted, this verifies our
/// decisions (whether to probe at all, which socket options, whether to authenticate) without a
/// network.
/// </summary>
public sealed class SmtpConnectivityProbeTests
{
    /// <summary>
    /// Exposes the protected <c>ExecuteAsync</c> a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
    /// normally only runs through <c>StartAsync</c>, so a test can await exactly one probe attempt.
    /// </summary>
    private sealed class TestableSmtpConnectivityProbe : SmtpConnectivityProbe
    {
        public TestableSmtpConnectivityProbe(IOptions<SmtpOptions> options, ISmtpTransportFactory transportFactory)
            : base(options, transportFactory, NullLogger<SmtpConnectivityProbe>.Instance)
        {
        }

        public Task RunOnceAsync(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);
    }

    private static SmtpOptions DefaultOptions() => new()
    {
        Host = "smtp.example.com",
        Port = 587,
        TimeoutSeconds = 5,
    };

    private static (TestableSmtpConnectivityProbe probe, FakeSmtpTransport transport) NewProbe(SmtpOptions options)
    {
        FakeSmtpTransport transport = new();
        TestableSmtpConnectivityProbe probe = new(Options.Create(options), new FakeSmtpTransportFactory(transport));

        return (probe, transport);
    }

    /// <summary>No host configured means no connection attempt at all.</summary>
    [Fact]
    public async Task ExecuteAsyncDoesNotConnectWhenSmtpIsNotConfigured()
    {
        // Arrange
        SmtpOptions options = new(); // Host empty by default -> IsConfigured false.
        (TestableSmtpConnectivityProbe probe, FakeSmtpTransport transport) = NewProbe(options);

        // Act
        await probe.RunOnceAsync(CancellationToken.None);

        // Assert
        transport.ConnectCall.Should().BeNull();
    }

    /// <summary>ProbeOnStartup false skips the connection attempt even though SMTP is configured.</summary>
    [Fact]
    public async Task ExecuteAsyncDoesNotConnectWhenProbeOnStartupIsDisabled()
    {
        // Arrange
        SmtpOptions options = DefaultOptions();
        options.ProbeOnStartup = false;
        (TestableSmtpConnectivityProbe probe, FakeSmtpTransport transport) = NewProbe(options);

        // Act
        await probe.RunOnceAsync(CancellationToken.None);

        // Assert
        transport.ConnectCall.Should().BeNull();
    }

    /// <summary>Configured and enabled: connects with the same socket-option rule SmtpEmailSender uses.</summary>
    [Fact]
    public async Task ExecuteAsyncConnectsWithStartTlsByDefault()
    {
        // Arrange
        (TestableSmtpConnectivityProbe probe, FakeSmtpTransport transport) = NewProbe(DefaultOptions());

        // Act
        await probe.RunOnceAsync(CancellationToken.None);

        // Assert
        transport.ConnectCall.Should().NotBeNull();
        transport.ConnectCall!.Value.options.Should().Be(SecureSocketOptions.StartTls);
    }

    /// <summary>DisableTls connects unencrypted, same explicit opt-in as SmtpEmailSender.</summary>
    [Fact]
    public async Task ExecuteAsyncConnectsUnencryptedWhenDisableTlsIsSet()
    {
        // Arrange
        SmtpOptions options = DefaultOptions();
        options.DisableTls = true;
        (TestableSmtpConnectivityProbe probe, FakeSmtpTransport transport) = NewProbe(options);

        // Act
        await probe.RunOnceAsync(CancellationToken.None);

        // Assert
        transport.ConnectCall!.Value.options.Should().Be(SecureSocketOptions.None);
    }

    /// <summary>A configured username authenticates, the point of the probe (§ SmtpConnectivityProbe remarks).</summary>
    [Fact]
    public async Task ExecuteAsyncAuthenticatesWhenAUserNameIsConfigured()
    {
        // Arrange
        SmtpOptions options = DefaultOptions();
        options.UserName = "relay-account";
        options.Password = "hunter2";
        (TestableSmtpConnectivityProbe probe, FakeSmtpTransport transport) = NewProbe(options);

        // Act
        await probe.RunOnceAsync(CancellationToken.None);

        // Assert
        transport.AuthenticateCall.Should().NotBeNull();
        transport.AuthenticateCall!.Value.userName.Should().Be("relay-account");
    }

    /// <summary>No username configured: connects without attempting to authenticate.</summary>
    [Fact]
    public async Task ExecuteAsyncDoesNotAuthenticateWithoutAUserName()
    {
        // Arrange
        (TestableSmtpConnectivityProbe probe, FakeSmtpTransport transport) = NewProbe(DefaultOptions());

        // Act
        await probe.RunOnceAsync(CancellationToken.None);

        // Assert
        transport.AuthenticateCall.Should().BeNull();
    }

    /// <summary>A connection failure does not throw out of ExecuteAsync - it's a diagnostic, never fatal.</summary>
    [Fact]
    public async Task ExecuteAsyncDoesNotThrowWhenConnectFails()
    {
        // Arrange
        (TestableSmtpConnectivityProbe probe, FakeSmtpTransport transport) = NewProbe(DefaultOptions());
        transport.ThrowOnConnect = new System.Net.Sockets.SocketException();

        // Act
        Func<Task> act = () => probe.RunOnceAsync(CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    /// <summary>An authentication failure does not throw out of ExecuteAsync either.</summary>
    [Fact]
    public async Task ExecuteAsyncDoesNotThrowWhenAuthenticateFails()
    {
        // Arrange
        SmtpOptions options = DefaultOptions();
        options.UserName = "relay-account";
        options.Password = "wrong";
        (TestableSmtpConnectivityProbe probe, FakeSmtpTransport transport) = NewProbe(options);
        transport.ThrowOnAuthenticate = new AuthenticationException("bad credentials");

        // Act
        Func<Task> act = () => probe.RunOnceAsync(CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    /// <summary>A successful probe disconnects cleanly.</summary>
    [Fact]
    public async Task ExecuteAsyncDisconnectsAfterASuccessfulProbe()
    {
        // Arrange
        (TestableSmtpConnectivityProbe probe, FakeSmtpTransport transport) = NewProbe(DefaultOptions());

        // Act
        await probe.RunOnceAsync(CancellationToken.None);

        // Assert
        transport.DisconnectedWithQuit.Should().Be(true);
    }
}
