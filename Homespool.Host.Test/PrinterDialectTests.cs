using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// Which variant of the Connect protocol a connection is speaking, and why silence is evidence.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three-way choice is tested here rather than only through the sender</b>, because it is the
/// thing that decides whether a file can reach a printer at all - and it got the wrong answer for the
/// Python SDK for as long as the pre-websocket transport existed, which nothing noticed because every
/// test of that path used firmware.
/// </para>
/// <para>
/// The distinction is only meaningful without a socket: a printer that can stream chunks is offered
/// the inline transfer and never asked anything else.
/// </para>
/// </remarks>
public class PrinterDialectTests
{
    /// <summary>
    /// A socket connection is Buddy whatever it announces, because nothing else opens one - and the
    /// questions the other dialects answer do not arise there.
    /// </summary>
    [Fact]
    public void ASocketConnectionIsBuddy()
    {
        PrinterDialect.For(PrinterClient.Anonymous(PrinterTransport.WebSocket), supportsInlineTransfer: true)
                      .Should().Be(PrinterDialect.BuddySocket);

        PrinterDialect.For(new PrinterClient(PrinterTransport.WebSocket, "anything at all"), supportsInlineTransfer: true)
                      .Should().Be(PrinterDialect.BuddySocket);
    }

    /// <summary>
    /// Buddy sends exactly Fingerprint and Token on this transport and no user agent of any kind
    /// (connect.cpp:137, read 2026-08-18), so saying nothing identifies it.
    /// </summary>
    [Fact]
    public void AClientThatAnnouncesNothingIsBuddy()
    {
        PrinterDialect.For(PrinterClient.Anonymous(PrinterTransport.Http), supportsInlineTransfer: false)
                      .Should().Be(PrinterDialect.BuddyHttp);

        PrinterDialect.BuddyHttp.UnderstandsEncryptedDownload
                      .Should().BeTrue("Buddy fetches AES-CTR ciphertext and decrypts it itself");
    }

    /// <summary>
    /// The case that could not receive a file at all: the SDK has no encrypted download and answers
    /// the command with nothing, so the send times out rather than failing.
    /// </summary>
    [Fact]
    public void AnAnnouncedClientIsTheSdk()
    {
        PrinterDialect dialect = PrinterDialect.For(
            new PrinterClient(PrinterTransport.Http, "Prusa-Connect-SDK-Printer/0.9.0"), supportsInlineTransfer: false);

        dialect.Should().Be(PrinterDialect.ConnectSdk);
        dialect.UnderstandsEncryptedDownload.Should().BeFalse();
        dialect.SupportsInlineTransfer.Should().BeFalse("the inline transfer needs a socket, and there is none");
    }

    /// <summary>
    /// The discriminator is the SDK's own product token, not "it said something". An agent nobody
    /// recognises keeps Buddy's path - which matters because a Buddy printer offered the SDK's
    /// download gets a command whose inline chunk request has no URL, and firmware asserts on it.
    /// </summary>
    [Theory]
    [InlineData("Prusa-Connect-SDK-Printer/0.9.0", false)]
    [InlineData("prusa-connect-sdk-printer/1.2.3", false)]
    [InlineData("PrusaLink/0.7.0", true)]
    [InlineData("Mozilla/5.0", true)]
    [InlineData("", true)]
    public void OnlyTheSdkNamedOutrightGetsThePlainDownload(string userAgent, bool expectedBuddy)
    {
        PrinterClient client = new(PrinterTransport.Http, userAgent.Length == 0 ? null : userAgent);

        PrinterDialect.For(client, supportsInlineTransfer: false)
                      .Should().Be(expectedBuddy ? PrinterDialect.BuddyHttp : PrinterDialect.ConnectSdk);
    }

    /// <summary>
    /// The version is learned from INFO rather than announced, so a client is knowable before it is
    /// fully known. Buddy sends no user agent at all, which makes the version the only thing that
    /// separates one Buddy from another - and what START_CONNECT_DOWNLOAD means moved twice across
    /// releases, so anything that ever needs to tell them apart needs this.
    /// </summary>
    [Fact]
    public void TheFirmwareVersionIsLearnedRatherThanAnnounced()
    {
        PrinterClient client = PrinterClient.Anonymous(PrinterTransport.Http);

        client.FirmwareVersion.Should().BeNull("nothing has described itself yet");

        PrinterClient described = client.WithFirmware("6.2.6");

        described.FirmwareVersion.Should().Be("6.2.6");
        described.Describe.Should().Contain("firmware 6.2.6");

        // The dialect does not move: the encrypted download works for every Buddy version, which is
        // why it is what Buddy is sent and why nothing branches on the version today.
        PrinterDialect.For(described, supportsInlineTransfer: false).Should().Be(PrinterDialect.BuddyHttp);
    }

    /// <summary>
    /// A repeated INFO must not churn the record, and a null must not erase what is known - a printer
    /// reports many events, and only some of them carry a version.
    /// </summary>
    [Fact]
    public void LearningTheSameVersionAgainChangesNothing()
    {
        PrinterClient client = PrinterClient.Anonymous(PrinterTransport.Http).WithFirmware("6.2.6");

        client.WithFirmware("6.2.6").Should().BeSameAs(client);
        client.WithFirmware(null).Should().BeSameAs(client, "an event carrying no version says nothing about it");
        client.WithFirmware("6.4.1").FirmwareVersion.Should().Be("6.4.1", "a printer that was upgraded says so");
    }

    /// <summary>
    /// An agent we do not recognise is worth saying out loud: it is treated as Buddy, and that is the
    /// assumption most likely to be wrong.
    /// </summary>
    [Fact]
    public void AnUnknownAgentIsFlaggedAsUnrecognised()
    {
        new PrinterClient(PrinterTransport.Http, "PrusaLink/0.7.0").IsUnrecognised.Should().BeTrue();
        new PrinterClient(PrinterTransport.Http, "Prusa-Connect-SDK-Printer/0.9.0").IsUnrecognised.Should().BeFalse();
        PrinterClient.Anonymous(PrinterTransport.Http).IsUnrecognised.Should().BeFalse("silence is Buddy, not a mystery");
    }

    /// <summary>
    /// An actor that reports nothing must not become the newest dialect by default. The plaintext
    /// download exists for a client that identifies itself; everything else keeps Buddy's path.
    /// </summary>
    [Fact]
    public void NoObservationAtAllIsBuddy()
    {
        PrinterDialect.For(client: null, supportsInlineTransfer: false).Should().Be(PrinterDialect.BuddyHttp);
    }
}
