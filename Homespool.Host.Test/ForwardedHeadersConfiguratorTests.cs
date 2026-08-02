using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// The mapping from this deployment's <see cref="XForwardedOptions"/> onto the framework's
/// forwarded-headers options.
/// </summary>
/// <remarks>
/// Worth testing directly because the whole feature is a trust boundary expressed as configuration:
/// a header believed from the wrong peer is an attacker choosing what the logs say and what goes in
/// a password-reset link.
/// </remarks>
public class ForwardedHeadersConfiguratorTests
{
    private static ForwardedHeadersOptions Apply(XForwardedOptions source, out List<string> ignored)
    {
        ForwardedHeadersOptions target = new();
        List<string> collected = [];

        ForwardedHeadersConfigurator.Apply(source, target, collected.Add);

        ignored = collected;
        return target;
    }

    /// <summary>
    /// The client address is read from <c>X-Real-IP</c>, not <c>X-Forwarded-For</c>.
    /// </summary>
    /// <remarks>
    /// The load-bearing one. <c>X-Forwarded-For</c> is a chain and the classic vulnerability is
    /// taking the wrong entry from it; <c>X-Real-IP</c> is a single value the proxy overwrites, so
    /// there is no entry to choose wrongly. A change back would be silent, hence the literal.
    /// </remarks>
    [Fact]
    public void TheClientAddressComesFromXRealIpByDefault()
    {
        // Assert
        Apply(new XForwardedOptions(), out _).ForwardedForHeaderName.Should().Be("X-Real-IP");
    }

    /// <summary>
    /// Scheme and host are forwarded too, not only the address.
    /// </summary>
    /// <remarks>
    /// The proto is what stops eight mail-link call sites emitting <c>http://</c> behind a
    /// TLS-terminating proxy; the host is what makes them name the address the user reached rather
    /// than the container's internal one.
    /// </remarks>
    [Fact]
    public void SchemeAndHostAreForwardedAsWellAsTheAddress()
    {
        // Act
        ForwardedHeadersOptions applied = Apply(new XForwardedOptions(), out _);

        // Assert
        applied.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedFor);
        applied.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedProto);
        applied.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedHost);
    }

    /// <summary>
    /// Configured proxies and networks replace the framework's loopback defaults rather than adding
    /// to them.
    /// </summary>
    /// <remarks>
    /// A deployment that has said which network its proxy is on does not also want loopback trusted.
    /// Note this is only safe because the caller leaves the middleware unregistered when nothing is
    /// configured - see <see cref="AnEmptyConfigurationTrustsNothingWhichIsWhyTheCallerMustNotRegisterIt"/>.
    /// </remarks>
    [Fact]
    public void ConfiguredEntriesReplaceTheDefaultsRatherThanExtendThem()
    {
        // Arrange
        XForwardedOptions source = new()
        {
            KnownProxies = ["203.0.113.7"],
            KnownNetworks = ["172.28.0.0/16"],
        };

        // Act
        ForwardedHeadersOptions applied = Apply(source, out _);

        // Assert
        applied.KnownProxies.Should().ContainSingle().Which.ToString().Should().Be("203.0.113.7");
        applied.KnownIPNetworks.Should().ContainSingle();
        applied.KnownIPNetworks.Single().ToString().Should().Be("172.28.0.0/16");
    }

    /// <summary>
    /// An unusable entry is skipped and reported, never thrown.
    /// </summary>
    /// <remarks>
    /// A typo in one environment variable must not put a deployment into a crash loop, and must not
    /// pass unnoticed either - a silently dropped proxy means forwarded headers stop being honoured
    /// and the only symptom is mail saying <c>http://</c>.
    /// </remarks>
    [Fact]
    public void UnparseableEntriesAreSkippedAndReported()
    {
        // Arrange
        XForwardedOptions source = new()
        {
            KnownProxies = ["not-an-ip", "203.0.113.7"],
            KnownNetworks = ["172.28.0.0/16", "10.0.0.0/notacidr"],
        };

        // Act
        ForwardedHeadersOptions applied = Apply(source, out List<string> ignored);

        // Assert
        applied.KnownProxies.Should().ContainSingle("the valid entry still applies");
        applied.KnownIPNetworks.Should().ContainSingle("the valid entry still applies");
        ignored.Should().HaveCount(2);
        ignored.Should().Contain(m => m.Contains("not-an-ip", System.StringComparison.Ordinal));
        ignored.Should().Contain(m => m.Contains("notacidr", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// With nothing configured the resulting options trust <b>nothing explicitly</b> — which is
    /// exactly why the caller must not register the middleware in that state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the trap the feature was built around, and it is counterintuitive enough to pin.
    /// ASP.NET performs its peer check only when at least one known proxy or network is present. Both
    /// lists empty therefore does <b>not</b> mean "trust nobody" - it means the check is skipped and
    /// forwarded headers are honoured from <i>any</i> client.
    /// </para>
    /// <para>
    /// Measured against a running server on 2026-07-29: unconfigured, a loopback client's
    /// <c>X-Forwarded-Proto: https</c> was honoured; with <c>10.0.0.0/8</c> trusted instead, the
    /// identical request was ignored. The build was clean and the suite green while that was true, so
    /// the guard is <see cref="XForwardedOptions.TrustsAnything"/> at the registration site rather
    /// than anything this method can do.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnEmptyConfigurationTrustsNothingWhichIsWhyTheCallerMustNotRegisterIt()
    {
        // Arrange
        XForwardedOptions source = new();

        // Act
        ForwardedHeadersOptions applied = Apply(source, out _);

        // Assert
        applied.KnownProxies.Should().BeEmpty();
        applied.KnownIPNetworks.Should().BeEmpty();
        source.TrustsAnything.Should().BeFalse(
            "an empty configuration disables ASP.NET's peer check rather than tightening it, so the "
            + "middleware must not be registered at all");
    }

    /// <summary>
    /// <see cref="XForwardedOptions.TrustsAnything"/> is true as soon as either list has an entry.
    /// </summary>
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, true)]
    public void TrustsAnythingReflectsEitherList(int proxies, int networks, bool expected)
    {
        // Arrange
        XForwardedOptions source = new()
        {
            KnownProxies = proxies == 0 ? [] : ["203.0.113.7"],
            KnownNetworks = networks == 0 ? [] : ["172.28.0.0/16"],
        };

        // Assert
        source.TrustsAnything.Should().Be(expected);
    }

    /// <summary>
    /// One proxy hop by default, matching one nginx in front.
    /// </summary>
    [Fact]
    public void OneHopIsForwardedByDefault()
    {
        // Assert
        Apply(new XForwardedOptions(), out _).ForwardLimit.Should().Be(1);
    }
}
