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
        PrinterDialect.For(PrinterClient.Anonymous(PrinterTransport.WebSocket), streamsChunks: true)
                      .Should().Be(PrinterDialect.BuddySocket);

        PrinterDialect.For(new PrinterClient(PrinterTransport.WebSocket, "anything at all"), streamsChunks: true)
                      .Should().Be(PrinterDialect.BuddySocket);
    }

    /// <summary>
    /// Buddy sends exactly Fingerprint and Token on this transport and no user agent of any kind
    /// (connect.cpp:137, read 2026-08-18), so saying nothing identifies it.
    /// </summary>
    [Fact]
    public void AClientThatAnnouncesNothingIsBuddy()
    {
        PrinterDialect.For(PrinterClient.Anonymous(PrinterTransport.Http), streamsChunks: false)
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
            new PrinterClient(PrinterTransport.Http, "Prusa-Connect-SDK-Printer/0.9.0"), streamsChunks: false);

        dialect.Should().Be(PrinterDialect.ConnectSdk);
        dialect.UnderstandsEncryptedDownload.Should().BeFalse();
        dialect.StreamsChunks.Should().BeFalse("there is no socket to pull chunks over");
    }

    /// <summary>
    /// An actor that reports nothing must not become the newest dialect by default. The plaintext
    /// download exists for a client that identifies itself; everything else keeps Buddy's path.
    /// </summary>
    [Fact]
    public void NoObservationAtAllIsBuddy()
    {
        PrinterDialect.For(client: null, streamsChunks: false).Should().Be(PrinterDialect.BuddyHttp);
    }
}
