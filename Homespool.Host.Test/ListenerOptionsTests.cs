using System;
using System.Collections.Generic;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using Homespool.Host.Listeners;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="ListenerOptions.Validate"/>: the boundary has to be able to exist before anything is
/// bound to it.
/// </summary>
/// <remarks>
/// Two credential classes on one port is not a degraded deployment, it is the segregation switched
/// off with every route answering on the one listener - so these assert a refusal to start rather
/// than a warning.
/// </remarks>
public class ListenerOptionsTests
{
    [Fact]
    public void TheShippedDefaultsDescribeABoundaryThatCanExist()
    {
        ListenerOptions options = new();

        Action validate = options.Validate;

        validate.Should().NotThrow();
    }

    /// <summary>
    /// No legacy listener is the default, and the one a deployment should keep.
    /// </summary>
    [Fact]
    public void ThereIsNoLegacyPrinterEndpointUnlessOneIsAskedFor()
    {
        new ListenerOptions().LegacyPrinterPort.Should().BeNull();
    }

    /// <summary>
    /// <b>The contract with <c>compose.yaml</c>, which is why this is a test rather than a reading of
    /// the binder.</b> The variable is passed unconditionally and is empty when nobody set it, so an
    /// empty string has to mean "no listener". If it ever threw instead, every deployment that had not
    /// asked for a legacy listener would fail to start - and the message would name a binder rather than
    /// the variable.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnsetLegacyPortMeansNoListenerRatherThanAStartupFailure(string? configured)
    {
        IConfiguration configuration = new ConfigurationBuilder()
                                       .AddInMemoryCollection(new Dictionary<string, string?>
                                       {
                                           ["Listeners:LegacyPrinterPort"] = configured,
                                       })
                                       .Build();

        ListenerOptions options = new();

        Action bind = () => configuration.GetSection(ListenerOptions.SectionName).Bind(options);

        bind.Should().NotThrow();
        options.LegacyPrinterPort.Should().BeNull();
    }

    [Fact]
    public void AConfiguredLegacyListenerOnItsOwnPortIsAccepted()
    {
        ListenerOptions options = new() { LegacyPrinterPort = 15800 };

        Action validate = options.Validate;

        validate.Should().NotThrow();
    }

    /// <summary>
    /// <b>The worst of the collisions</b>: the printer protocol is unauthenticated until its own
    /// handler runs, so sharing the user port would put it on the listener browsers reach.
    /// </summary>
    [Fact]
    public void ALegacyListenerSharingTheUserPortIsRefused()
    {
        ListenerOptions options = new() { UserPort = 8080, LegacyPrinterPort = 8080 };

        Action validate = options.Validate;

        validate.Should().Throw<InvalidOperationException>().WithMessage("*LegacyPrinterPort*");
    }

    /// <summary>
    /// Quieter and still worth refusing: the deployment believes it has opened a plaintext listener and
    /// has not, so a printer provisioned onto it reaches a port that answers over TLS.
    /// </summary>
    [Fact]
    public void ALegacyListenerSharingThePrinterPortIsRefused()
    {
        ListenerOptions options = new() { PrinterPort = 15443, LegacyPrinterPort = 15443 };

        Action validate = options.Validate;

        validate.Should().Throw<InvalidOperationException>().WithMessage("*LegacyPrinterPort*");
    }

    [Fact]
    public void ALegacyListenerSharingTheTransferPortIsRefused()
    {
        ListenerOptions options = new() { TransferPort = 15080, LegacyPrinterPort = 15080 };

        Action validate = options.Validate;

        validate.Should().Throw<InvalidOperationException>().WithMessage("*LegacyPrinterPort*");
    }

    [Fact]
    public void ALegacyListenerSharingTheUserHttpsPortIsRefused()
    {
        ListenerOptions options = new() { UserHttpsPort = 8443, LegacyPrinterPort = 8443 };

        Action validate = options.Validate;

        validate.Should().Throw<InvalidOperationException>().WithMessage("*LegacyPrinterPort*");
    }
}
