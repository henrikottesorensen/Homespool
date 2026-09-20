using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.Mail;

namespace Homespool.Host.Test;

/// <summary>
/// The queue in front of the two anonymous forms' mail: that queueing sends nothing, and what
/// happens when the queue fills or a send goes wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is being protected here is a silence.</b> Both forms answer identically for a registered
/// and an unregistered address so as not to say which addresses exist, and with SMTP configured the
/// path that had mail to send also took measurably longer to answer - a whole SMTP conversation
/// against one indexed lookup. So the first test is the one that matters: the caller's thread sends
/// nothing, whatever the address turned out to be.
/// </para>
/// <para>
/// <b>Nothing here polls.</b> Stopping the service completes the queue and drains it, so every test
/// asserts after a start-stop rather than after a wait, and a change that made the loop stop
/// draining fails outright instead of going slow.
/// </para>
/// </remarks>
public sealed class DeferredEmailSenderTests
{
    /// <summary>
    /// Comfortably more than the queue holds, without naming the capacity: what these tests are
    /// about is which messages survive being flooded, not the number.
    /// </summary>
    private const int Flood = 200;

    [Fact]
    public async Task QueueingSendsNothingOnTheCallersThread()
    {
        // Arrange
        using Rig rig = new();

        // Act
        rig.Sender.Enqueue("who@example.com", "Reset your password", "<p>link</p>");

        // Assert
        rig.Mail.SentEmails.Should().BeEmpty("the answer must not wait on the mail server");

        await rig.RunToCompletionAsync();

        rig.Mail.SentEmails.Should().ContainSingle("the loop is what sends it");
    }

    [Fact]
    public async Task AQueuedMessageIsSentAsItWasGiven()
    {
        // Arrange
        using Rig rig = new();

        // Act
        rig.Sender.Enqueue("who@example.com", "Reset your password", "<p>link</p>");
        await rig.RunToCompletionAsync();

        // Assert
        (string email, string subject, string htmlMessage) sent = rig.Mail.SentEmails.Should().ContainSingle().Subject;
        sent.email.Should().Be("who@example.com");
        sent.subject.Should().Be("Reset your password");
        sent.htmlMessage.Should().Be("<p>link</p>");
    }

    /// <summary>
    /// Stopping drains what was queued rather than discarding it - a message accepted from a request
    /// that has already been answered has nowhere else to come from.
    /// </summary>
    [Fact]
    public async Task StoppingSendsWhatIsStillQueued()
    {
        // Arrange
        using Rig rig = new();
        await rig.Sender.StartAsync(CancellationToken.None);

        // Act
        rig.Sender.Enqueue("first@example.com", "Reset your password", "<p>link</p>");
        rig.Sender.Enqueue("second@example.com", "Reset your password", "<p>link</p>");

        await rig.Sender.StopAsync(CancellationToken.None);

        // Assert
        rig.Mail.SentEmails.Select(sent => sent.email).Should()
           .Equal("first@example.com", "second@example.com");

        // The loop must have ended by running out of queue. Canceled here is the .NET 10 shape this
        // class carries an entered-signal for: a stop that reached the loop before its first
        // statement, which abandons everything queued and logs nothing.
        rig.Sender.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion);
    }

    /// <summary>
    /// A full queue drops the message arriving, not the ones already waiting, and says which
    /// recipient lost one - nobody else is ever told.
    /// </summary>
    [Fact]
    public async Task AFullQueueDropsTheArrivingMessageAndSaysWhichOne()
    {
        // Arrange
        using Rig rig = new();

        // Act - nothing is draining yet, so the queue fills
        for (int index = 0; index < Flood; index++)
        {
            rig.Sender.Enqueue(Recipient(index), "Reset your password", "<p>link</p>");
        }

        await rig.RunToCompletionAsync();

        // Assert
        rig.Mail.SentEmails.Should().HaveCountLessThan(Flood, "the queue is bounded");
        rig.Mail.SentEmails.Select(sent => sent.email).Should()
           .Equal(Enumerable.Range(0, rig.Mail.SentEmails.Count).Select(Recipient),
                  "the ones that survive are the ones that were already waiting, in order");

        rig.Logger.Collector.GetSnapshot().Should()
           .ContainSingle(record => ReportsDropOf(record, Recipient(Flood - 1)),
                          "the message that arrived at a full queue is the one reported dropped");
    }

    /// <summary>
    /// A send that throws must not end the loop: nothing restarts a background service, so every
    /// later reset and confirmation mail would go unsent with nothing to say why.
    /// </summary>
    [Fact]
    public async Task AThrowingSendIsLoggedAndTheNextMessageStillGoes()
    {
        // Arrange
        ThrowOnceEmailSender throwing = new();
        using Rig rig = new(throwing);

        // Act
        rig.Sender.Enqueue("first@example.com", "Reset your password", "<p>link</p>");
        rig.Sender.Enqueue("second@example.com", "Reset your password", "<p>link</p>");

        await rig.RunToCompletionAsync();

        // Assert
        throwing.Accepted.Should().Equal("second@example.com");
        rig.Logger.Collector.GetSnapshot().Should()
           .ContainSingle(record => record.Level == LogLevel.Error).Which
           .StructuredState.Should()
           .Contain(property => property.Key == "Email" && property.Value == "first@example.com");
    }

    /// <summary>
    /// A refused send is a warning naming the recipient, since by now the request that asked for it
    /// has long been answered and told nothing.
    /// </summary>
    [Fact]
    public async Task ARefusedSendIsLoggedAgainstItsRecipient()
    {
        // Arrange
        using Rig rig = new();
        rig.Mail.Result = EmailSendResult.Failed;

        // Act
        rig.Sender.Enqueue("who@example.com", "Reset your password", "<p>link</p>");
        await rig.RunToCompletionAsync();

        // Assert
        rig.Logger.Collector.GetSnapshot().Should()
           .ContainSingle(record => record.Level == LogLevel.Warning).Which
           .StructuredState.Should()
           .Contain(property => property.Key == "Email" && property.Value == "who@example.com");
    }

    private static string Recipient(int index)
    {
        return string.Create(CultureInfo.InvariantCulture, $"queued-{index}@example.com");
    }

    /// <summary>
    /// Whether <paramref name="record"/> is the queue-is-full warning for <paramref name="email"/>.
    /// The capacity is what tells it apart from the other warnings that name a recipient.
    /// </summary>
    private static bool ReportsDropOf(FakeLogRecord record, string email)
    {
        return record.Level == LogLevel.Warning &&
               Names(record, "Capacity") &&
               Names(record, "Email", email);
    }

    private static bool Names(FakeLogRecord record, string key, string? value = null)
    {
        return record.StructuredState?
                     .Any(property => property.Key == key && (value is null || property.Value == value)) == true;
    }

    /// <summary>
    /// The sender, the scope factory it resolves <see cref="IEmailSender"/> through, and whatever
    /// that resolves to - all of which have to be disposed together.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Rig(IEmailSender? registered = null)
        {
            ServiceCollection services = new();
            services.AddScoped<IEmailSender>(_ => registered ?? Mail);

            _provider = services.BuildServiceProvider();
            Sender = new DeferredEmailSender(_provider.GetRequiredService<IServiceScopeFactory>(), Logger);
        }

        public CapturingEmailSender Mail { get; } = new();

        public FakeLogger<DeferredEmailSender> Logger { get; } = new();

        public DeferredEmailSender Sender { get; }

        /// <summary>
        /// Starts the loop and stops it again, which completes the queue and drains it. The token is
        /// deliberately <see cref="CancellationToken.None"/>: what the stop is being asked to do here
        /// is drain, and a cancelled one would be testing the opposite.
        /// </summary>
        public async Task RunToCompletionAsync()
        {
            await Sender.StartAsync(CancellationToken.None);
            await Sender.StopAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            Sender.Dispose();
            _provider.Dispose();
        }
    }

    /// <summary>An <see cref="IEmailSender"/> whose first send throws and whose later ones do not.</summary>
    private sealed class ThrowOnceEmailSender : IEmailSender
    {
        private bool _thrown;

        public List<string> Accepted { get; } = [];

        public Task<EmailSendResult> SendEmailAsync(string email, string subject, string htmlMessage)
        {
            if (!_thrown)
            {
                _thrown = true;

                throw new InvalidOperationException("the mail server hung up");
            }

            Accepted.Add(email);

            return Task.FromResult(EmailSendResult.Sent);
        }
    }
}
