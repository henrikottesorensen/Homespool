using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Homespool.Host.Test;

/// <summary>
/// A push service in a message handler: it accepts what Homespool posts, records it, and answers with
/// whatever status a test asks for. Paired with <see cref="FakePushBrowser"/>, which holds the keys a
/// real browser would and can read what was sent to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The decryption here is written from RFC 8291's text, not borrowed from the library under test</b>,
/// and <c>WebPushChannelTests</c> checks it against the RFC's own worked example. A round trip through
/// the library's encryptor and the library's decryptor would agree with any mistake the two shared;
/// this one agrees only with the RFC.
/// </para>
/// <para>
/// Plugged in as the named client's primary handler, so everything above it - the bounded error body,
/// the library, the channel - runs as it does in production. The address guard, which is the primary
/// handler's connect callback, is replaced with it and is tested on its own.
/// </para>
/// </remarks>
internal sealed class FakePushService : HttpMessageHandler
{
    /// <summary>A push service host on the built-in allowlist, so no configuration is needed.</summary>
    public const string Host = "fcm.googleapis.com";

    private readonly ConcurrentQueue<FakePush> _received = new();

    /// <summary>The status every request is answered with, unless <see cref="Respond"/> is set.</summary>
    public HttpStatusCode Answer { get; set; } = HttpStatusCode.Created;

    /// <summary>Builds the whole response, for a test that needs headers or a body.</summary>
    public Func<HttpResponseMessage>? Respond { get; set; }

    /// <summary>How long to take over answering, for a test of a push service that is slow.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>Every request received, oldest first.</summary>
    public IReadOnlyList<FakePush> Received => [.. _received];

    /// <summary>A new browser with its own keys and an endpoint on <see cref="Host"/>.</summary>
    public static FakePushBrowser NewBrowser()
    {
        return new FakePushBrowser($"https://{Host}/fcm/send/{Guid.NewGuid():N}");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                 CancellationToken cancellationToken)
    {
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        _received.Enqueue(new FakePush(request.RequestUri!, headers, body));

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Respond is not null)
        {
            return Respond();
        }

        return new HttpResponseMessage(Answer);
    }
}

/// <summary>What one request to the <see cref="FakePushService"/> carried.</summary>
/// <param name="Endpoint">Where it was posted.</param>
/// <param name="Headers">Request and content headers, by name.</param>
/// <param name="Body">The encrypted body as sent.</param>
internal sealed record FakePush(Uri Endpoint, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    /// <summary>A header's value, or null.</summary>
    public string? Header(string name)
    {
        return Headers.TryGetValue(name, out string? value) ? value : null;
    }

    /// <summary>
    /// Checks the VAPID <c>Authorization</c> header against <paramref name="publicKey"/> and returns its
    /// claims. Throws when the key named is another, or the signature does not verify.
    /// </summary>
    /// <param name="publicKey">The deployment's public key, base64url.</param>
    public JsonElement VerifiedVapidClaims(string publicKey)
    {
        string authorization = Header(HeaderNames.Authorization) ?? throw new InvalidOperationException("No Authorization header.");

        if (!authorization.StartsWith("vapid ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Not the vapid scheme: {authorization}");
        }

        Dictionary<string, string> parameters = authorization["vapid ".Length..]
                                                .Split(',', StringSplitOptions.TrimEntries)
                                                .Select(part => part.Split('=', 2))
                                                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

        if (parameters["k"] != publicKey)
        {
            throw new InvalidOperationException("The request names a different public key.");
        }

        string[] token = parameters["t"].Split('.');
        byte[] point = WebEncoders.Base64UrlDecode(publicKey);

        using ECDsa verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
        });

        bool verified = verifier.VerifyData(Encoding.ASCII.GetBytes(token[0] + "." + token[1]),
                                            WebEncoders.Base64UrlDecode(token[2]),
                                            HashAlgorithmName.SHA256);

        if (!verified)
        {
            throw new InvalidOperationException("The VAPID signature does not verify.");
        }

        return JsonDocument.Parse(WebEncoders.Base64UrlDecode(token[1])).RootElement.Clone();
    }
}

/// <summary>
/// The browser half of a push subscription: a P-256 key pair, an authentication secret and an
/// endpoint, and the RFC 8291 decryption a browser would run on what arrives.
/// </summary>
internal sealed class FakePushBrowser : IDisposable
{
    private readonly ECDiffieHellman _key;
    private readonly byte[] _publicKey;
    private readonly byte[] _auth;

    public FakePushBrowser(string endpoint)
        : this(endpoint, ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), RandomNumberGenerator.GetBytes(16))
    {
    }

    /// <summary>A browser with the given keys - RFC 8291's example, for checking this class itself.</summary>
    public FakePushBrowser(string endpoint, ECDiffieHellman key, byte[] auth)
    {
        ArgumentNullException.ThrowIfNull(key);

        Endpoint = endpoint;
        _key = key;
        _auth = auth;

        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
        _publicKey = [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
    }

    public string Endpoint { get; }

    /// <summary>The public key, base64url, as a subscription's <c>keys.p256dh</c>.</summary>
    public string P256dh => WebEncoders.Base64UrlEncode(_publicKey);

    /// <summary>The secret, base64url, as a subscription's <c>keys.auth</c>.</summary>
    public string Auth => WebEncoders.Base64UrlEncode(_auth);

    /// <summary>
    /// Decrypts an <c>aes128gcm</c> body sent to this browser, per RFC 8291 section 3.4 and RFC 8188.
    /// </summary>
    public byte[] Decrypt(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);

        byte[] salt = body[..16];
        int recordSize = (body[16] << 24) | (body[17] << 16) | (body[18] << 8) | body[19];
        int keyIdLength = body[20];
        byte[] serverPublicKey = body[21..(21 + keyIdLength)];
        byte[] cipherText = body[(21 + keyIdLength)..];

        if (keyIdLength != 65 || cipherText.Length > recordSize)
        {
            throw new CryptographicException($"Not a single-record Web Push body: key id {keyIdLength} bytes, record size {recordSize}.");
        }

        using ECDiffieHellman server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublicKey[1..33], Y = serverPublicKey[33..] },
        });

        byte[] sharedSecret = _key.DeriveRawSecretAgreement(server.PublicKey);
        byte[] keyPrk = HMACSHA256.HashData(_auth, sharedSecret);
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. _publicKey, .. serverPublicKey, 0x01];
        byte[] ikm = HMACSHA256.HashData(keyPrk, keyInfo);
        byte[] prk = HMACSHA256.HashData(salt, ikm);
        byte[] contentKeyInfo = [.. "Content-Encoding: aes128gcm\0"u8, 0x01];
        byte[] nonceInfo = [.. "Content-Encoding: nonce\0"u8, 0x01];
        byte[] contentKey = HMACSHA256.HashData(prk, contentKeyInfo)[..16];
        byte[] nonce = HMACSHA256.HashData(prk, nonceInfo)[..12];

        byte[] plain = new byte[cipherText.Length - 16];

        using (AesGcm gcm = new(contentKey, 16))
        {
            gcm.Decrypt(nonce, cipherText[..^16], cipherText[^16..], plain);
        }

        // Padding is zeros after the delimiter; the last record's delimiter is 0x02.
        int end = plain.Length - 1;

        while (end >= 0 && plain[end] == 0)
        {
            end--;
        }

        if (end < 0 || plain[end] != 0x02)
        {
            throw new CryptographicException("The record does not end with the last-record delimiter.");
        }

        return plain[..end];
    }

    /// <summary>The body decrypted and read as the JSON the service worker receives.</summary>
    public JsonElement DecryptJson(byte[] body)
    {
        return JsonDocument.Parse(Decrypt(body)).RootElement.Clone();
    }

    public void Dispose()
    {
        _key.Dispose();
    }
}
