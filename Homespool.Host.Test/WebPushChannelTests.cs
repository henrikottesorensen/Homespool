using System;
using System.Collections.Generic;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.Notifications;
using Homespool.Host.Notifications.WebPush;
using Homespool.Host.PrusaConnect;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Delivering to a browser: what reaches the push service, whether the browser could read it, and how
/// each answer a push service can give becomes an outcome.
/// </summary>
/// <remarks>
/// <b>Three of these exist because the library's defaults are unsafe</b>, and each would pass against a
/// channel that forgot to override one: the 429 that must be sent once, the payload that must be
/// cut to fit before sending, and the error body that must not be read without a bound.
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

    private static NotificationMessage Message(string title = "Core One needs you", string body = "Replace filament.")
    {
        return new NotificationMessage(title, body, "/Printers/Detail/3", "printer-3",
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
        push.Header("Topic").Should().BeNull("Apple's push service refuses a push that carries one");

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
    /// The largest message the limits allow: title and body past their caps and all outside ASCII, so
    /// every character is six bytes of JSON, and the address and tag at theirs. It is sent with the
    /// body at its cap, cut no further, and fits the body a push service must accept.
    /// </summary>
    [Fact]
    public async Task TheLargestMessageTheLimitsAllowIsSentWhole()
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        NotificationMessage message = new(new string('ø', WebPushChannel.MaxTitleLength * 2),
                                          new string('ø', WebPushChannel.MaxBodyLength * 2),
                                          "/" + new string('x', NotificationMessage.MaxUrlLength - 1),
                                          new string('x', NotificationMessage.MaxTagLength),
                                          NotificationUrgency.High,
                                          TimeSpan.FromMinutes(10));

        DeliveryOutcome outcome = await DeliverAsync(Destination(browser), message);

        outcome.Should().Be(DeliveryOutcome.Delivered);

        FakePush push = _rig.PushService.Received.Single();
        push.Body.Length.Should().BeLessThanOrEqualTo(4096, "RFC 8030 obliges a push service to take 4096 bytes and no more");
        browser.DecryptJson(push.Body).GetProperty("body").GetString().Should().HaveLength(WebPushChannel.MaxBodyLength);
    }

    [Fact]
    public void ABodyThatFitsIsLeftAlone()
    {
        string body = new('ø', WebPushChannel.MaxBodyLength);

        WebPushChannel.FitBody("Core One needs you", body, "/Printers/Detail/3", "printer-3", WebPushChannel.MaxPayloadBytes)
                      .Should().BeSameAs(body);
    }

    /// <summary>
    /// What loosening a limit would cost: a body cut shorter, never a refusal that counts against the
    /// browser.
    /// </summary>
    [Fact]
    public void ABodyIsCutFurtherWhenTheRestLeavesItTooLittleRoom()
    {
        const int MaxBytes = 1000;
        string body = new('ø', WebPushChannel.MaxBodyLength);

        string fitted = WebPushChannel.FitBody("Core One needs you", body, "/Printers/Detail/3", "printer-3", MaxBytes);

        fitted.Length.Should().BeLessThan(body.Length);
        fitted.Should().EndWith("…");
        JsonSerializer.SerializeToUtf8Bytes(new { title = "Core One needs you", body = fitted, url = "/Printers/Detail/3", tag = "printer-3" })
                      .Length.Should().BeLessThanOrEqualTo(MaxBytes);
    }

    [Fact]
    public void APayloadThatCannotFitWithNoBodyIsAProgrammingError()
    {
        Action fit = () => WebPushChannel.FitBody("Core One needs you", "Replace filament.", "/Printers/Detail/3", "printer-3", 20);

        fit.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(WebPushChannel.MaxTitleLength, WebPushChannel.MaxTitleLength)]
    [InlineData(WebPushChannel.MaxTitleLength + 1, WebPushChannel.MaxTitleLength)]
    public async Task ATitleIsCutToFit(int length, int expectedLength)
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        await DeliverAsync(Destination(browser), Message(title: new string('x', length)));

        string title = browser.DecryptJson(_rig.PushService.Received.Single().Body).GetProperty("title").GetString()!;
        title.Length.Should().Be(expectedLength);
        title.EndsWith('…').Should().Be(length > WebPushChannel.MaxTitleLength);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(WebPushChannel.MaxBodyLength, WebPushChannel.MaxBodyLength)]
    [InlineData(WebPushChannel.MaxBodyLength + 1, WebPushChannel.MaxBodyLength)]
    public async Task ABodyIsCutToFit(int length, int expectedLength)
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        await DeliverAsync(Destination(browser), Message(body: new string('x', length)));

        string body = browser.DecryptJson(_rig.PushService.Received.Single().Body).GetProperty("body").GetString()!;
        body.Length.Should().Be(expectedLength);
        body.EndsWith('…').Should().Be(length > WebPushChannel.MaxBodyLength);
    }

    /// <summary>
    /// The longest a printer can make a notification: its longest name in the title and its longest
    /// attention text as the body, all of both outside ASCII, so each character is six bytes of JSON.
    /// Uncut it is over 4 KB, and refusing it five times running would remove the browser.
    /// </summary>
    [Fact]
    public async Task ThePrintersLongestMessageIsDelivered()
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        string name = new('ø', Printer.NameMaxLength);
        string attention = new('ø', PrusaConnectConstants.AttentionTextMaxLength);

        DeliveryOutcome outcome = await DeliverAsync(Destination(browser),
                                                     Message(title: $"{name} stopper snart for et filamentskift", body: attention));

        outcome.Should().Be(DeliveryOutcome.Delivered);
    }

    [Fact]
    public void ACutNeverEndsHalfWayThroughACharacter()
    {
        // An emoji, two UTF-16 units, straddling the cut.
        string text = new string('x', WebPushChannel.MaxBodyLength - 2) + "😀" + "tail";

        WebPushChannel.Shorten(text, WebPushChannel.MaxBodyLength)
                      .Should().Be(new string('x', WebPushChannel.MaxBodyLength - 2) + "…");
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

    /// <summary>
    /// A push service whose name resolves only to this machine is refused by the address guard, and
    /// told apart from an outage: the fault is the destination's, not the network's.
    /// </summary>
    /// <remarks>
    /// Through the production handler, because what is pinned is how <c>SocketsHttpHandler</c> hands
    /// on an exception from its connect callback, which a fake handler could only imitate. A guard
    /// that let the connection through would end in <see cref="DeliveryOutcome.Transient"/> instead,
    /// whatever did or did not answer on this machine's port 443.
    /// </remarks>
    [Fact]
    public async Task AnEndpointResolvingToThisMachineIsRefusedByTheAddressGuard()
    {
        // Arrange
        string databasePath = WebPushRig.NewDatabasePath();
        using FakePushBrowser browser = new("https://localhost/push/1");

        try
        {
            await using WebPushRig rig = await WebPushRig.CreateAsync(
                databasePath,
                new EphemeralDataProtectionProvider(),
                new Dictionary<string, string?> { [$"{WebPushOptions.SectionName}:{nameof(WebPushOptions.AdditionalEndpointHosts)}:0"] = "localhost" },
                realNetwork: true);

            // Act
            DeliveryOutcome outcome = await rig.Services.GetRequiredService<WebPushChannel>()
                                               .DeliverAsync(Destination(browser), Message(), TestContext.Current.CancellationToken);

            // Assert
            outcome.Should().Be(DeliveryOutcome.Refused);
        }
        finally
        {
            WebPushRig.Delete(databasePath);
        }
    }

    /// <summary>
    /// A push service that cannot be reached is an outage, which never counts towards removing a
    /// browser: a phone out of signal has not gone anywhere.
    /// </summary>
    [Fact]
    public async Task APushServiceThatCannotBeReachedIsAnOutage()
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        _rig.PushService.Respond = () => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused.");

        DeliveryOutcome outcome = await DeliverAsync(Destination(browser));

        outcome.Should().Be(DeliveryOutcome.Transient);
    }

    /// <summary>
    /// A push service that takes longer than the client's timeout is an outage too - the timeout
    /// arrives as a cancellation, and one nobody asked for.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task APushServiceThatDoesNotAnswerInTimeIsAnOutage()
    {
        // Arrange
        string databasePath = WebPushRig.NewDatabasePath();
        using FakePushBrowser browser = FakePushService.NewBrowser();

        try
        {
            await using WebPushRig rig = await WebPushRig.CreateAsync(
                databasePath,
                new EphemeralDataProtectionProvider(),
                services: services => services.AddHttpClient(WebPushChannel.HttpClientName,
                                                              client => client.Timeout = TimeSpan.FromMilliseconds(200)));

            rig.PushService.Delay = TimeSpan.FromMinutes(1);

            // Act
            DeliveryOutcome outcome = await rig.Services.GetRequiredService<WebPushChannel>()
                                               .DeliverAsync(Destination(browser), Message(), TestContext.Current.CancellationToken);

            // Assert
            outcome.Should().Be(DeliveryOutcome.Transient);
        }
        finally
        {
            WebPushRig.Delete(databasePath);
        }
    }

    /// <summary>
    /// A delivery the caller cuts short - the service shutting down - has no outcome. Answered as an
    /// outage, it would mark a browser that was working as failing.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ADeliveryTheCallerCancelsHasNoOutcome()
    {
        // Arrange
        using FakePushBrowser browser = FakePushService.NewBrowser();
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Made first, so the cancellation lands on the request rather than on making the key.
        await _rig.Services.GetRequiredService<VapidKeyStore>().GetAsync(TestContext.Current.CancellationToken);

        _rig.PushService.Delay = TimeSpan.FromMinutes(1);
        stopping.CancelAfter(TimeSpan.FromMilliseconds(200));

        // Act
        Func<Task> delivering = () => _rig.Services.GetRequiredService<WebPushChannel>()
                                          .DeliverAsync(Destination(browser), Message(), stopping.Token);

        // Assert
        await delivering.Should().ThrowAsync<OperationCanceledException>();
        _rig.PushService.Received.Should().ContainSingle("the request was on its way when it was cancelled");
    }

    /// <summary>
    /// Keys that passed the check when they were stored but cannot be encrypted to now are refused, not
    /// thrown: one bad row must not stop the rest of an account's browsers hearing.
    /// </summary>
    [Fact]
    public async Task KeysThatCannotBeEncryptedToAreRefusedWithoutARequest()
    {
        using FakePushBrowser browser = FakePushService.NewBrowser();

        byte[] offCurve = WebEncoders.Base64UrlDecode(browser.P256dh);
        offCurve[^1] ^= 0x01;

        WebPushDestination destination = Destination(browser);
        destination.P256dh = WebEncoders.Base64UrlEncode(offCurve);

        DeliveryOutcome outcome = await DeliverAsync(destination);

        outcome.Should().Be(DeliveryOutcome.Refused);
        _rig.PushService.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task ADestinationOfAnotherKindIsAProgrammingError()
    {
        await FluentActions.Awaiting(() => _rig.Services.GetRequiredService<WebPushChannel>()
                                               .DeliverAsync(new Elsewhere { Name = "Elsewhere" }, Message(), TestContext.Current.CancellationToken))
                           .Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// What a push service says when it refuses is logged beside the status, because the status does
    /// not say what was wrong - and the endpoint, which the same exception carries, is not.
    /// </summary>
    [Fact]
    public async Task APushServicesReasonForARefusalIsLoggedAndItsEndpointIsNot()
    {
        // Arrange
        string databasePath = WebPushRig.NewDatabasePath();
        using FakePushBrowser browser = FakePushService.NewBrowser();
        FakeLogger<WebPushChannel> logger = new();

        try
        {
            await using WebPushRig rig = await WebPushRig.CreateAsync(
                databasePath,
                new EphemeralDataProtectionProvider(),
                services: services => services.AddSingleton<ILogger<WebPushChannel>>(logger));

            rig.PushService.Respond = () => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"reason\":\"BadWebPushTopic\"}"),
            };

            // Act
            DeliveryOutcome outcome = await rig.Services.GetRequiredService<WebPushChannel>()
                                               .DeliverAsync(Destination(browser), Message(), TestContext.Current.CancellationToken);

            // Assert
            outcome.Should().Be(DeliveryOutcome.Refused);

            FakeLogRecord refusal = logger.Collector.GetSnapshot().Should().ContainSingle().Subject;
            refusal.Message.Should().Contain("BadWebPushTopic");
            refusal.Message.Should().NotContain(browser.Endpoint);
        }
        finally
        {
            WebPushRig.Delete(databasePath);
        }
    }

    /// <summary>
    /// An answer as long as the push service cares to make it is cut, and says how long it was.
    /// </summary>
    [Fact]
    public async Task ALongAnswerIsCutInTheLog()
    {
        // Arrange
        string databasePath = WebPushRig.NewDatabasePath();
        using FakePushBrowser browser = FakePushService.NewBrowser();
        FakeLogger<WebPushChannel> logger = new();

        try
        {
            await using WebPushRig rig = await WebPushRig.CreateAsync(
                databasePath,
                new EphemeralDataProtectionProvider(),
                services: services => services.AddSingleton<ILogger<WebPushChannel>>(logger));

            rig.PushService.Respond = () => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(new string('x', 5000)),
            };

            // Act
            await rig.Services.GetRequiredService<WebPushChannel>()
                     .DeliverAsync(Destination(browser), Message(), TestContext.Current.CancellationToken);

            // Assert
            string message = logger.Collector.GetSnapshot().Should().ContainSingle().Subject.Message;
            message.Should().Contain("<5000 characters in all>");
            message.Length.Should().BeLessThan(600);
        }
        finally
        {
            WebPushRig.Delete(databasePath);
        }
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

    /// <summary>A destination of a kind the Web Push channel does not deliver to.</summary>
    private sealed class Elsewhere() : NotificationDestination(NotificationChannelKind.Undefined);

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
