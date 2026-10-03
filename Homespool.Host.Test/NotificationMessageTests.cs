using System;

using AwesomeAssertions;

using Homespool.Host.Health;
using Homespool.Host.Notifications;

namespace Homespool.Host.Test;

/// <summary>
/// The bounds on a message's address and tag: short, and only characters JSON leaves as they are, so
/// a channel with a size limit never has to cut either.
/// </summary>
public class NotificationMessageTests
{
    private static NotificationMessage Message(string url = "/Printers/Detail/3", string tag = "printer-3")
    {
        return new NotificationMessage("Core One needs you", "Replace filament.", url, tag,
                                       NotificationUrgency.High, TimeSpan.FromMinutes(10));
    }

    /// <summary>Every address and tag the application builds today, at its longest.</summary>
    [Theory]
    [InlineData("/Printers/Detail/ffffffff-ffff-ffff-ffff-ffffffffffff", "printer--9223372036854775808")]
    [InlineData(NotificationDestinationService.SettingsPath, NotificationDestinationService.TestTag)]
    [InlineData(TelemetryAlertService.PushUrl, TelemetryAlertService.HealthTag)]
    public void TheApplicationsOwnAddressesAndTagsAreAccepted(string url, string tag)
    {
        NotificationMessage message = Message(url, tag);

        message.Url.Should().Be(url);
        message.Tag.Should().Be(tag);
    }

    [Fact]
    public void AnAddressAtItsLimitIsAcceptedAndOnePastItIsNot()
    {
        string atLimit = "/" + new string('x', NotificationMessage.MaxUrlLength - 1);

        Message(url: atLimit).Url.Should().Be(atLimit);

        Action pastLimit = () => Message(url: atLimit + "x");
        pastLimit.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ATagAtItsLimitIsAcceptedAndOnePastItIsNot()
    {
        string atLimit = new('x', NotificationMessage.MaxTagLength);

        Message(tag: atLimit).Tag.Should().Be(atLimit);

        Action pastLimit = () => Message(tag: atLimit + "x");
        pastLimit.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Every printable ASCII character JSON escapes, and a few that are not ASCII at all: each would
    /// cost a payload more than the one byte the limits are counted in.
    /// </summary>
    [Theory]
    [InlineData("\"")]
    [InlineData("&")]
    [InlineData("'")]
    [InlineData("+")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("\\")]
    [InlineData("`")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("ø")]
    [InlineData("…")]
    public void AnAddressOrTagWithACharacterJsonEscapesIsRefused(string character)
    {
        Action url = () => Message(url: "/Printers/Detail/" + character);
        Action tag = () => Message(tag: "printer-" + character);

        url.Should().Throw<ArgumentException>();
        tag.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ACopyIsHeldToTheSameBounds()
    {
        NotificationMessage message = Message();

        Action url = () => _ = message with { Url = "/Printers/Detail/<3>" };
        Action tag = () => _ = message with { Tag = new string('x', NotificationMessage.MaxTagLength + 1) };

        url.Should().Throw<ArgumentException>();
        tag.Should().Throw<ArgumentException>();
    }
}
