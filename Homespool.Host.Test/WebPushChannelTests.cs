using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Notifications;
using Homespool.Host.Notifications.WebPush;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Delivering to a browser: what reaches the push service, whether the browser could read it, and how
/// each answer a push service can give becomes an outcome.
/// </summary>
/// <remarks>
/// <b>Three of these exist because the library's defaults are unsafe</b>, and each would pass against a
/// channel that forgot to override one: the 429 that must be sent once, the payload that must be
/// measured before sending, and the error body that must not be read without a bound.
/// </remarks>
public sealed class WebPushChannelTests : IAsyncLifetime
{
    private readonly string _databasePath = WebPushRig.NewDatabasePath();
    private WebPushRig _rig = null!;

    public async ValueTask InitializeAsync()
    {
        _rig = await WebPushRig.CreateAsync(_databasePath, new EphemeralDataProtectionProvider());
    }

    public async ValueTask DisposeAsync()
    {
        await _rig.DisposeAsync();
        WebPushRig.Delete(_databasePath);
    }

    private static NotificationMessage Message(string body = "Replace filament.", string tag = "printer-3")
    {
        return new NotificationMessage("Core One needs you", body, "/Printers/Detail/3", tag,
                                       NotificationUrgency.High, TimeSpan.FromMinutes(10));
    }

    private static WebPushDestination Destination(FakePushBrowser browser)
    {
        return new WebPushDestination
        {
            Endpoint = browser.Endpoint,
            P256dh = browser.P256dh,
            Auth = browser.Auth,
            Name = "Test browser",
        };
    }

    private Task<DeliveryOutcome> DeliverAsync(WebPushDestination destination, NotificationMessage? message = null)
    {
        return _rig.Services.GetRequiredService<WebPushChannel>()
                   .DeliverAsync(destination, message ?? Message(), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The test's own decryption against RFC 8291's worked example, so that everything below which
    /// trusts it is trusting the RFC and not the library it is checking.
    /// </summary>
    [Fact]
    public void TheTestBrowserDecryptsRfc8291sWorkedExample()
    {
        byte[] uaPublic = WebEncoders.Base64UrlDecode("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");

        ECDiffieHellman key = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = WebEncoders.Base64UrlDecode("q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94"),
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..] },
        });

        using FakePushBrowser browser = new("https://push.example/", key, WebEncoders.Base64UrlDecode("BTBZMqHH6r4Tts7J_aSIgg"));

        byte[] body = [.. WebEncoders.Base64UrlDecode("DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8"),
                       .. WebEncoders.Base64UrlDecode("8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ")];

        Encoding.UTF8.GetString(browser.Decrypt(body)).Should().Be("When I grow up, I want to be a watermelon");
    }

    [Fact]
    public async Task AMessageArrivesReadableByTheBrowserAndSignedByTheDeployment()
    {
        // Arrange
        using FakePushBrowser browser = FakePushService.NewBrowser();
        string publicKey = (await _rig.Services.GetRequiredService<VapidKeyStore>()
                                      .GetAsync(TestContext.Current.CancellationToken)).PublicKey;

        // Act
        DeliveryOutcome outcome = await DeliverAsync(Destination(browser));

        // Assert
        outcome.Should().Be(DeliveryOutcome.Delivered);

        FakePush push = _rig.PushService.Received.Should().ContainSingle().Subject;

        push.Endpoint.Should().Be(new Uri(browser.Endpoint));

        JsonElement payload = browser.DecryptJson(push.Body);
        payload.GetProperty("title").GetString().Should().Be("Core One needs you");
        payload.GetProperty("body").GetString().Should().Be("Replace filament.");
        payload.GetProperty("url").GetString().Should().Be("/Printers/Detail/3");
        payload.GetProperty("tag").GetString().Should().Be("printer-3");

        push.Header("Content-Encoding").Should().Be("aes128gcm");
        push.Header("TTL").Should().Be("600");
        push.Header("Urgency").Should().Be("high");
        push.Header("Topic").Should().Be("printer-3");

        JsonElement claims = push.VerifiedVapidClaims(publicKey);
        claims.GetProperty("aud").GetString().Should().Be("https://fcm.googleapis.com");
        claims.GetProperty("sub").GetString().Should().Be(WebPushOptions.DefaultContact);

        DateTimeOffset expires = DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64());
        expires.Should().BeAfter(DateTimeOffset.UtcNow).And.BeBefore(DateTimeOffset.UtcNow.AddHours(24),
                                                                     "a push service refuses a token valid for longer than a day");
    }

    /// <summary>
    /// Every answer a push service gives, sent exactly once - a 429 included, which the library would
    /// retry for as long as the service kept asking - which is why this has a timeout: without the
    /// override it does not fail, it never finishes.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(201, DeliveryOutcome.Delivered)]
    [InlineData(202, DeliveryOutcome.Delivered)]
    [InlineData(404, DeliveryOutcome.Gone)]
    [InlineData(410, DeliveryOutcome.Gone)]
    [InlineData(408, DeliveryOutcome.Transient)]
    [InlineData(429, DeliveryOutcome.Transient)]
    [InlineData(500, DeliveryOutcome.Transient)]
    [InlineData(503, DeliveryOutcome.Transient)]
    [InlineData(400, DeliveryOutcome.Refused)]
    [InlineData(403, DeliveryOutcome.Refused)]
    [InlineData(413, DeliveryOutcome.Refused)]
    public async Task EachAnswerBecomesAnOutcomeAfterOneRequest(int status, DeliveryOutcome expected)
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        _rig.PushService.Respond = () =>
        {
            HttpResponseMessage response = new((HttpStatusCode)status);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));

            return response;
        };

        DeliveryOutcome outcome = await DeliverAsync(Destination(browser));

        outcome.Should().Be(expected);
        _rig.PushService.Received.Should().HaveCount(1, "nothing is retried inside a delivery");
    }

    [Fact]
    public async Task AnEndpointOffTheListIsRefusedWithoutARequest()
    {
        using FakePushBrowser browser = new("https://push.attacker.example/x");

        DeliveryOutcome outcome = await DeliverAsync(Destination(browser));

        outcome.Should().Be(DeliveryOutcome.Refused);
        _rig.PushService.Received.Should().BeEmpty();
    }

    /// <summary>
    /// A payload the push service would refuse is refused here instead, where the reason can be
    /// logged, rather than sent and answered with a 413 that says nothing about why.
    /// </summary>
    [Fact]
    public async Task AMessageTooLargeToDeliverIsRefusedWithoutARequest()
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        DeliveryOutcome outcome = await DeliverAsync(Destination(browser), Message(body: new string('x', WebPushChannel.MaxPayloadBytes)));

        outcome.Should().Be(DeliveryOutcome.Refused);
        _rig.PushService.Received.Should().BeEmpty();
    }

    /// <summary>
    /// The largest message that fits does fit: the limit is the encrypted body's, not a guess.
    /// </summary>
    [Fact]
    public async Task AMessageAtTheLimitIsSentAndFitsTheBody()
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        int overhead = JsonSerializer.SerializeToUtf8Bytes(new { title = "Core One needs you", body = string.Empty, url = "/Printers/Detail/3", tag = "printer-3" }).Length;
        string body = new('x', WebPushChannel.MaxPayloadBytes - overhead);

        DeliveryOutcome outcome = await DeliverAsync(Destination(browser), Message(body: body));

        outcome.Should().Be(DeliveryOutcome.Delivered);
        _rig.PushService.Received.Single().Body.Length.Should().Be(4096, "RFC 8030 obliges a push service to take 4096 bytes and no more");
    }

    /// <summary>
    /// An error response whose body never ends. The library reads it whole, with no token and past the
    /// client's timeout; the bound on the named client is what lets this finish at all.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnEndlessErrorBodyDoesNotHoldTheDeliveryOpen()
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        _rig.PushService.Respond = () => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(new EndlessStream()),
        };

        DeliveryOutcome outcome = await DeliverAsync(Destination(browser));

        outcome.Should().Be(DeliveryOutcome.Refused);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("a-tag-that-is-far-longer-than-thirty-two")]
    public async Task ATagAPushServiceWouldRefuseIsAProgrammingError(string tag)
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        await FluentActions.Awaiting(() => DeliverAsync(Destination(browser), Message(tag: tag)))
                           .Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// A subscription's keys as the subscribe form receives them: a genuine pair, and each way one can
    /// be wrong - including a point off the curve, which RFC 8291 requires be refused.
    /// </summary>
    [Fact]
    public void OnlyAPointOnTheCurveAndASixteenByteSecretAreAccepted()
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        byte[] point = WebEncoders.Base64UrlDecode(browser.P256dh);
        byte[] offCurve = [.. point];
        offCurve[^1] ^= 0x01;

        WebPushSubscriptionKeys.AreValid(browser.P256dh, browser.Auth).Should().BeTrue();

        WebPushSubscriptionKeys.AreValid(WebEncoders.Base64UrlEncode(offCurve), browser.Auth).Should().BeFalse("the point is not on P-256");
        WebPushSubscriptionKeys.AreValid(WebEncoders.Base64UrlEncode(point[..33]), browser.Auth).Should().BeFalse("a compressed or truncated key is not what a browser sends");
        WebPushSubscriptionKeys.AreValid("not base64url!", browser.Auth).Should().BeFalse();
        WebPushSubscriptionKeys.AreValid(null, browser.Auth).Should().BeFalse();
        WebPushSubscriptionKeys.AreValid(browser.P256dh, WebEncoders.Base64UrlEncode(new byte[15])).Should().BeFalse();
        WebPushSubscriptionKeys.AreValid(browser.P256dh, null).Should().BeFalse();
    }

    /// <summary>A stream that produces bytes for as long as anybody reads it.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'x');

            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
