using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Controllers;
using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.E2ETest;

/// <summary>
/// <c>GET /f/&lt;iv&gt;/raw</c> - the encrypted download a printer on the pre-websocket transport
/// fetches over a plain HTTP connection of its own - driven exactly as firmware drives it
/// (<c>download.cpp:199-244</c> at v6.2.6) and decrypted with the same cipher, so what is asserted is
/// the bytes and not the status.
/// </summary>
/// <remarks>
/// <para>
/// A direct <see cref="HttpClient"/> rather than the fake, on purpose: the fake's download model is
/// the inline engine, and teaching it to fetch is its own piece of work. What this proves is the
/// endpoint - the contract firmware checks, the cipher offset on a resumed range, the two stores
/// agreeing - which a client that decrypts and compares can prove more sharply than a client that
/// merely arrives.
/// </para>
/// <para>
/// The IV is the capability and the request is anonymous, so there is no enrolment here at all: an
/// offer, a key, a GET.
/// </para>
/// </remarks>
public sealed class EncryptedTransferEndpointTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("e2e-enc");
    private readonly CapturingSink _logs = new();
    private HomespoolFactory _factory = null!;
    private string? _offerDirectory;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch, null, _logs);
        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _factory?.Dispose();
        if (_offerDirectory is not null && Directory.Exists(_offerDirectory))
        {
            Directory.Delete(_offerDirectory, recursive: true);
        }

        _scratch.Dispose();
    }

    /// <summary>
    /// A fetch from zero: 200, the exact length, the mode header echoed, and a body that decrypts to
    /// the file - which is what proves the key and IV registered are the ones the cipher ran under.
    /// </summary>
    [Fact]
    public async Task AFullFetchIsServedAsCiphertextThatDecryptsToTheFile()
    {
        // Arrange
        byte[] plaintext = RandomNumberGenerator.GetBytes(100_000);
        (string ivHex, byte[] key, byte[] iv) = OfferEncrypted(plaintext);

        using HttpClient printer = PrinterListener.CreateTransferClient(_factory);

        // Act
        using HttpRequestMessage request = FirmwareRequest(ivHex, "bytes=0-");
        using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "a fetch from zero is a 200, which is what firmware's first request expects");
        response.Content.Headers.ContentLength.Should().Be(plaintext.Length, "firmware requires Content-Length");
        response.Headers.GetValues(EncryptedTransferController.ContentEncryptionModeHeader)
                .Should().ContainSingle().Which.Should().Be(EncryptedTransferController.AesCtr,
                                                            "firmware fails any other mode outright");

        byte[] body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        body.Should().NotEqual(plaintext, "what crosses the plain listener must be ciphertext");

        using TransferCipher cipher = new(key, iv, 0);
        cipher.Transform(body);

        body.Should().Equal(plaintext, "and decrypting with the registered key and IV yields the file");
    }

    /// <summary>
    /// A resumed fetch: 206 - firmware fails a resumed download answered 200 - and a body that
    /// decrypts correctly <b>from that offset</b>, which is the cipher's counter being seeded right.
    /// Getting the offset wrong produces plausible ciphertext that decrypts to garbage, so this is the
    /// assertion the resume path lives or dies by.
    /// </summary>
    [Fact]
    public async Task AResumedFetchIsA206WhoseBodyDecryptsFromTheOffset()
    {
        // Arrange
        byte[] plaintext = RandomNumberGenerator.GetBytes(100_000);
        (string ivHex, byte[] key, byte[] iv) = OfferEncrypted(plaintext);
        const long start = 65_536; // A sector-aligned resume point, as firmware sends.

        using HttpClient printer = PrinterListener.CreateTransferClient(_factory);

        // Act
        using HttpRequestMessage request = FirmwareRequest(ivHex, $"bytes={start}-");
        using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.PartialContent, "download.cpp:234 fails a resumed download that is answered 200");
        response.Content.Headers.ContentLength.Should().Be(plaintext.Length - start);
        response.Content.Headers.ContentRange!.ToString().Should().Be($"bytes {start}-{plaintext.Length - 1}/{plaintext.Length}");

        byte[] body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        using TransferCipher cipher = new(key, iv, start);
        cipher.Transform(body);

        body.Should().Equal(plaintext.AsSpan((int)start).ToArray(), "the counter was seeded for the offset, not for zero");
    }

    /// <summary>
    /// An IV nothing was registered under is a 404, and so is one that was revoked: to the printer,
    /// both are a transfer that failed, and neither leaks whether an offer ever existed.
    /// </summary>
    [Fact]
    public async Task AnUnknownOrRevokedIvIsNotFound()
    {
        // Arrange
        byte[] plaintext = RandomNumberGenerator.GetBytes(4096);
        (string ivHex, byte[] _, byte[] _) = OfferEncrypted(plaintext);

        using HttpClient printer = PrinterListener.CreateTransferClient(_factory);

        // Act - unknown
        using HttpRequestMessage unknown = FirmwareRequest(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), "bytes=0-");
        using HttpResponseMessage unknownResponse = await printer.SendAsync(unknown, TestContext.Current.CancellationToken);

        // Act - revoked
        _factory.Services.GetRequiredService<EncryptedTransferOffers>().Revoke(ivHex);
        _factory.Services.GetRequiredService<ITransferOffers>().Revoke(ivHex);

        using HttpRequestMessage revoked = FirmwareRequest(ivHex, "bytes=0-");
        using HttpResponseMessage revokedResponse = await printer.SendAsync(revoked, TestContext.Current.CancellationToken);

        // Assert
        unknownResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        revokedResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A start the cipher cannot begin at - not a multiple of the AES block - is refused as
    /// unsatisfiable rather than served. Firmware never sends one; a client that does would otherwise
    /// receive ciphertext that decrypts to nothing, silently.
    /// </summary>
    [Fact]
    public async Task AnUnalignedRangeStartIsRefused()
    {
        // Arrange
        (string ivHex, byte[] _, byte[] _) = OfferEncrypted(RandomNumberGenerator.GetBytes(4096));

        using HttpClient printer = PrinterListener.CreateTransferClient(_factory);

        // Act
        using HttpRequestMessage request = FirmwareRequest(ivHex, "bytes=7-");
        using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    /// <summary>
    /// The endpoint lives on the transfer listener alone: on the printer listener and the user
    /// listener it does not exist, so neither the TLS door nor the application surface serves the
    /// one deliberately plain thing.
    /// </summary>
    [Fact]
    public async Task TheEndpointIsAbsentFromEveryOtherListener()
    {
        // Arrange
        (string ivHex, byte[] _, byte[] _) = OfferEncrypted(RandomNumberGenerator.GetBytes(4096));

        using HttpClient onPrinterListener = PrinterListener.CreateClient(_factory);
        using HttpClient onUserListener = _factory.CreateClient();

        // Act
        using HttpRequestMessage viaPrinter = FirmwareRequest(ivHex, "bytes=0-");
        using HttpResponseMessage printerResponse = await onPrinterListener.SendAsync(viaPrinter, TestContext.Current.CancellationToken);

        using HttpRequestMessage viaUser = FirmwareRequest(ivHex, "bytes=0-");
        using HttpResponseMessage userResponse = await onUserListener.SendAsync(viaUser, TestContext.Current.CancellationToken);

        // Assert
        printerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        userResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The request exactly as <c>download.cpp:199</c> composes it: <c>GET</c>, <c>Host</c>, the mode
    /// header, one <c>Range</c>, and no credential of any kind.
    /// </summary>
    private static HttpRequestMessage FirmwareRequest(string ivHex, string range)
    {
        HttpRequestMessage request = new(HttpMethod.Get, $"/f/{ivHex}/raw");

        request.Headers.TryAddWithoutValidation(EncryptedTransferController.ContentEncryptionModeHeader, EncryptedTransferController.AesCtr);
        request.Headers.Range = RangeHeaderValue.Parse(range);

        return request;
    }

    /// <summary>
    /// Does what <see cref="Homespool.Host.Printing.PrintFileSender"/> does for an HTTP printer, minus the command: writes
    /// the bytes, offers them under the IV's hex, and registers the key beside the offer.
    /// </summary>
    private (string ivHex, byte[] key, byte[] iv) OfferEncrypted(byte[] plaintext)
    {
        _offerDirectory ??= Path.Combine(Path.GetTempPath(), $"hs-e2e-enc-offer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_offerDirectory);

        byte[] key = RandomNumberGenerator.GetBytes(TransferCipher.KeyLength);
        byte[] iv = RandomNumberGenerator.GetBytes(TransferCipher.IvLength);
        string ivHex = Convert.ToHexStringLower(iv);

        string path = Path.Combine(_offerDirectory, $"{ivHex}.gcode");
        File.WriteAllBytes(path, plaintext);

        // Any printer id, as long as the two agree: the fetch opens the offer under the id the
        // registration carries, and no printer exists in this suite's database to match it against.
        const int offeredTo = 1;

        _factory.Services.GetRequiredService<ITransferOffers>().Offer(ivHex, path, offeredTo).Should().BeTrue();
        _factory.Services.GetRequiredService<EncryptedTransferOffers>().Register(ivHex, key, ivHex, offeredTo);

        return (ivHex, key, iv);
    }
}
