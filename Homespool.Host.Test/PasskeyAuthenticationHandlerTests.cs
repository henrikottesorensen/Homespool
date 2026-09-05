using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The passkey scheme, driven end to end in-process: a challenge on one request, an assertion signed
/// by <see cref="FakeAuthenticator"/> on the next, and what the handler makes of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refusals are the tests that matter</b>, as with the token handler: a handler that
/// authenticated every assertion would pass the success case. Each refusal below was checked by
/// mutation - a wrong challenge, a stale cookie, a replayed one, a foreign origin, an unverified
/// user, a credential the challenge's account does not hold - and each fails when its check is
/// removed from the handler or the engine.
/// </para>
/// <para>
/// <b>Two requests, two contexts, one cookie carried by hand.</b> The ceremony state lives in a cookie
/// the handler owns, so the second request is built from the first's <c>Set-Cookie</c>. Nothing here
/// touches <c>SignInManager</c>, which is the point.
/// </para>
/// </remarks>
public sealed class PasskeyAuthenticationHandlerTests : IDisposable
{
    private const string RelyingPartyId = "homespool.test";
    private const string LoginPath = "/Account/Login";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-passkeyauth-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // ---------- the challenge ----------
    [Fact]
    public async Task AChallengeAnswersRequestOptionsAndStartsACeremony()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        (PasskeyAuthenticationHandler handler, DefaultHttpContext request) = await rig.NewRequestAsync();

        // Act
        await handler.ChallengeAsync(new AuthenticationProperties());
        string body = await rig.BodyOf(request);

        // Assert
        request.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        request.Response.ContentType.Should().StartWith("application/json");
        request.Response.Headers.CacheControl.ToString().Should().Be("no-store");

        using JsonDocument options = JsonDocument.Parse(body);
        options.RootElement.GetProperty("rpId").GetString().Should().Be(RelyingPartyId);
        options.RootElement.GetProperty("challenge").GetString().Should().NotBeNullOrEmpty();
        options.RootElement.GetProperty("userVerification").GetString().Should().Be("required");
        options.RootElement.GetProperty("allowCredentials").GetArrayLength().Should().Be(0, "a challenge naming no account lets the browser offer whatever it holds");

        string setCookie = request.Response.Headers.SetCookie.ToString();
        setCookie.Should().StartWith(PasskeyAuthenticationOptions.DefaultCeremonyCookieName + "=");
        setCookie.Should().Contain("httponly");
        setCookie.Should().Contain("samesite=strict");
        setCookie.Should().Contain($"path={LoginPath}", "the cookie is scoped to the page that issued the challenge");
    }

    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("homespool.lan")]
    [InlineData("localhost")]
    [InlineData("nothomespool.test")]
    public async Task AChallengeIsWithheldOnAHostTheRelyingPartyIdDoesNotCover(string host)
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        (PasskeyAuthenticationHandler handler, DefaultHttpContext request) = await rig.NewRequestAsync(host: host);

        // Act
        await handler.ChallengeAsync(new AuthenticationProperties());

        // Assert
        request.Response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
        request.Response.Headers.SetCookie.ToString().Should().BeEmpty("no ceremony starts on a host the browser would refuse");
    }

    [Fact]
    public async Task AChallengeIsWithheldWhenNoRelyingPartyIdIsConfigured()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this, relyingPartyId: null);
        (PasskeyAuthenticationHandler handler, DefaultHttpContext request) = await rig.NewRequestAsync();

        // Act
        await handler.ChallengeAsync(new AuthenticationProperties());

        // Assert
        request.Response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
    }

    /// <summary>A subdomain of the relying-party id is covered, which is the one way one name serves two hosts.</summary>
    [Fact]
    public async Task AChallengeIsIssuedOnASubdomainOfTheRelyingPartyId()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        (PasskeyAuthenticationHandler handler, DefaultHttpContext request) = await rig.NewRequestAsync(host: "app." + RelyingPartyId);

        // Act
        await handler.ChallengeAsync(new AuthenticationProperties());

        // Assert
        request.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    // ---------- what the assertion path does not claim ----------
    [Fact]
    public async Task ARequestWithoutAnAssertionYieldsNoResult()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync();

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.None.Should().BeTrue("no assertion was posted, so the scheme has nothing to say");
    }

    // ---------- assertions it refuses ----------
    [Fact]
    public async Task AnAssertionWithoutACeremonyFails()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new();
        HSUser user = await rig.EnrolAsync(authenticator);

        // A challenge somebody else took, answered on a request that never saw its cookie.
        (PasskeyAuthenticationHandler challengeHandler, DefaultHttpContext challenge) = await rig.NewRequestAsync();
        await challengeHandler.ChallengeAsync(new AuthenticationProperties());
        string credential = authenticator.Assert(await rig.BodyOf(challenge), user.Id.ToString());

        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync(credential: credential);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse();
        result.None.Should().BeFalse("an assertion was posted, so this is a refusal rather than silence");
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task AnAssertionForAnotherChallengeFails()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new();
        HSUser user = await rig.EnrolAsync(authenticator);

        (PasskeyAuthenticationHandler firstHandler, DefaultHttpContext first) = await rig.NewRequestAsync();
        await firstHandler.ChallengeAsync(new AuthenticationProperties());
        (PasskeyAuthenticationHandler secondHandler, DefaultHttpContext second) = await rig.NewRequestAsync();
        await secondHandler.ChallengeAsync(new AuthenticationProperties());

        // The second challenge's answer, presented with the first ceremony's cookie.
        string credential = authenticator.Assert(await rig.BodyOf(second), user.Id.ToString());
        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync(credential: credential, cookie: Rig.CookieOf(first));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task AStaleCeremonyFails()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new();
        HSUser user = await rig.EnrolAsync(authenticator);

        (PasskeyAuthenticationHandler challengeHandler, DefaultHttpContext challenge) = await rig.NewRequestAsync();
        await challengeHandler.ChallengeAsync(new AuthenticationProperties());
        string credential = authenticator.Assert(await rig.BodyOf(challenge), user.Id.ToString());

        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync(credential: credential, cookie: Rig.CookieOf(challenge));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("expired");
    }

    [Fact]
    public async Task ACeremonyIsAnsweredOnce()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new();
        HSUser user = await rig.EnrolAsync(authenticator);

        (PasskeyAuthenticationHandler challengeHandler, DefaultHttpContext challenge) = await rig.NewRequestAsync();
        await challengeHandler.ChallengeAsync(new AuthenticationProperties());
        string credential = authenticator.Assert(await rig.BodyOf(challenge), user.Id.ToString());
        string cookie = Rig.CookieOf(challenge);

        (PasskeyAuthenticationHandler firstHandler, DefaultHttpContext first) = await rig.NewRequestAsync(credential: credential, cookie: cookie);
        AuthenticateResult firstResult = await firstHandler.AuthenticateAsync();

        // The browser honoured the deletion; a replay is the cookie presented again regardless.
        (PasskeyAuthenticationHandler replayHandler, _) = await rig.NewRequestAsync(credential: credential, cookie: cookie);

        // Act
        AuthenticateResult replay = await replayHandler.AuthenticateAsync();

        // Assert
        firstResult.Succeeded.Should().BeTrue("the first answer is the real one");
        first.Response.Headers.SetCookie.ToString().Should().Contain("expires=", "the ceremony cookie is deleted the moment it is read");
        replay.Succeeded.Should().BeFalse(
            "the ceremony was spent server-side with the first answer; the sign count is zero on both, "
            + "as it is on every synced authenticator, so nothing else would notice the repeat");
    }

    [Fact]
    public async Task AnAssertionFromAnotherOriginFails()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new();
        HSUser user = await rig.EnrolAsync(authenticator);

        (PasskeyAuthenticationHandler challengeHandler, DefaultHttpContext challenge) = await rig.NewRequestAsync();
        await challengeHandler.ChallengeAsync(new AuthenticationProperties());

        authenticator.Origin = "https://evil.test";
        string credential = authenticator.Assert(await rig.BodyOf(challenge), user.Id.ToString());
        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync(credential: credential, cookie: Rig.CookieOf(challenge));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse("phishing resistance is origin binding, and this is where it lives");
    }

    [Fact]
    public async Task AnAssertionWithoutUserVerificationFails()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new();
        HSUser user = await rig.EnrolAsync(authenticator);

        (PasskeyAuthenticationHandler challengeHandler, DefaultHttpContext challenge) = await rig.NewRequestAsync();
        await challengeHandler.ChallengeAsync(new AuthenticationProperties());

        authenticator.UserVerified = false;
        string credential = authenticator.Assert(await rig.BodyOf(challenge), user.Id.ToString());
        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync(credential: credential, cookie: Rig.CookieOf(challenge));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse("presence alone is not the policy; a face, a finger or a passcode is");
    }

    [Fact]
    public async Task AnAssertionNamingNoAccountFails()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new();
        await rig.EnrolAsync(authenticator);

        (PasskeyAuthenticationHandler challengeHandler, DefaultHttpContext challenge) = await rig.NewRequestAsync();
        await challengeHandler.ChallengeAsync(new AuthenticationProperties());

        string credential = authenticator.Assert(await rig.BodyOf(challenge), userHandle: null);
        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync(credential: credential, cookie: Rig.CookieOf(challenge));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse("a challenge that named no account needs the credential to say whose it is");
    }

    [Fact]
    public async Task AChallengeBoundToAnAccountRefusesAnotherAccountsPasskey()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new();
        HSUser owner = await rig.EnrolAsync(authenticator);
        HSUser other = await rig.AddUserAsync("other@example.com");

        AuthenticationProperties bound = new();
        bound.Items[PasskeyAuthenticationHandler.UserIdProperty] = other.Id.ToString();

        (PasskeyAuthenticationHandler challengeHandler, DefaultHttpContext challenge) = await rig.NewRequestAsync();
        await challengeHandler.ChallengeAsync(bound);

        string credential = authenticator.Assert(await rig.BodyOf(challenge), owner.Id.ToString());
        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync(credential: credential, cookie: Rig.CookieOf(challenge));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse("the ceremony was for one account and a different account's key answered");
    }

    [Fact]
    public async Task AnAssertionOnAHostTheRelyingPartyIdDoesNotCoverFails()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new();
        HSUser user = await rig.EnrolAsync(authenticator);

        (PasskeyAuthenticationHandler challengeHandler, DefaultHttpContext challenge) = await rig.NewRequestAsync();
        await challengeHandler.ChallengeAsync(new AuthenticationProperties());
        string credential = authenticator.Assert(await rig.BodyOf(challenge), user.Id.ToString());

        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync(credential: credential, cookie: Rig.CookieOf(challenge), host: "homespool.lan");

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse();
    }

    // ---------- the assertion it accepts ----------
    [Fact]
    public async Task AGoodAssertionAuthenticatesAsThePasskeysOwner()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new() { SignCount = 3 };
        HSUser user = await rig.EnrolAsync(authenticator);

        (PasskeyAuthenticationHandler challengeHandler, DefaultHttpContext challenge) = await rig.NewRequestAsync();
        await challengeHandler.ChallengeAsync(new AuthenticationProperties());

        authenticator.SignCount = 4;
        string credential = authenticator.Assert(await rig.BodyOf(challenge), user.Id.ToString());
        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync(credential: credential, cookie: Rig.CookieOf(challenge));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();
        UserPasskeyInfo? stored = await rig.Users.GetPasskeyAsync(user, authenticator.CredentialId);

        // Assert
        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.Ticket!.AuthenticationScheme.Should().Be(Schemes.Passkey);
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
        result.Principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(user.Id.ToString());
        result.Principal.FindFirstValue(ClaimTypes.AuthenticationMethod).Should().Be(PasskeyAuthenticationHandler.AuthenticationMethod);
        result.Ticket.Properties.Items.Should().ContainKey(PasskeyAuthenticationHandler.CredentialIdProperty);
        stored!.SignCount.Should().Be(4, "the ceremony is not complete until the counter is written back");
    }

    /// <summary>
    /// A challenge bound to an account accepts that account's own key: the shape a later
    /// re-authentication uses, where the page already knows who it is talking to.
    /// </summary>
    [Fact]
    public async Task AChallengeBoundToAnAccountAcceptsItsOwnPasskey()
    {
        // Arrange
        await using Rig rig = await Rig.CreateAsync(this);
        using FakeAuthenticator authenticator = new();
        HSUser user = await rig.EnrolAsync(authenticator);

        AuthenticationProperties bound = new();
        bound.Items[PasskeyAuthenticationHandler.UserIdProperty] = user.Id.ToString();

        (PasskeyAuthenticationHandler challengeHandler, DefaultHttpContext challenge) = await rig.NewRequestAsync();
        await challengeHandler.ChallengeAsync(bound);
        string body = await rig.BodyOf(challenge);

        string credential = authenticator.Assert(body, user.Id.ToString());
        (PasskeyAuthenticationHandler handler, _) = await rig.NewRequestAsync(credential: credential, cookie: Rig.CookieOf(challenge));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        using JsonDocument options = JsonDocument.Parse(body);
        options.RootElement.GetProperty("allowCredentials").GetArrayLength().Should().Be(1, "a bound challenge names the account's credentials");
        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(user.Id.ToString());
    }

    /// <summary>
    /// A real Identity stack over a migrated database, a relying-party id, and the two requests of a
    /// ceremony built the way the handler will see them.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly PasskeyAuthenticationHandlerTests _owner;
        private readonly HomespoolDbContext _context;
        private readonly IServiceProvider _provider;

        private Rig(PasskeyAuthenticationHandlerTests owner, HomespoolDbContext context, IServiceProvider provider, UserManager<HSUser> users)
        {
            _owner = owner;
            _context = context;
            _provider = provider;
            Users = users;
        }

        public UserManager<HSUser> Users { get; }

        public static async Task<Rig> CreateAsync(PasskeyAuthenticationHandlerTests owner, string? relyingPartyId = RelyingPartyId)
        {
            DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                           .UseSqlite($"Data Source={owner._databasePath}")
                                                           .Options;

            HomespoolDbContext context = new(options);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

            (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(
                context,
                services =>
                {
                    services.Configure<Middleware.SecurityOptions>(security => security.PasskeyServerDomain = relyingPartyId);
                    services.Configure<PasskeyAuthenticationOptions>(Schemes.Passkey, scheme => scheme.TimeProvider = owner._clock);
                });

            return new Rig(owner, context, provider, users);
        }

        /// <summary>
        /// A request to the login page on <paramref name="host"/>, carrying an assertion and a
        /// ceremony cookie when given them, and the handler initialised over it.
        /// </summary>
        public async Task<(PasskeyAuthenticationHandler handler, DefaultHttpContext request)> NewRequestAsync(
            string? credential = null,
            string? cookie = null,
            string host = RelyingPartyId)
        {
            DefaultHttpContext request = new()
            {
                RequestServices = _provider,
            };

            request.Request.Scheme = "https";
            request.Request.Host = new HostString(host);
            request.Request.Path = LoginPath;
            request.Request.Headers.Origin = $"https://{host}";
            request.Response.Body = new MemoryStream();

            if (credential is not null)
            {
                request.Request.Method = HttpMethods.Post;
                request.Request.ContentType = "application/x-www-form-urlencoded";
                request.Request.Form = new FormCollection(new Dictionary<string, StringValues>
                {
                    [PasskeyAuthenticationOptions.CredentialFormField] = credential,
                });
            }

            if (cookie is not null)
            {
                request.Request.Headers.Cookie = cookie;
            }

            PasskeyAuthenticationHandler handler = ActivatorUtilities.CreateInstance<PasskeyAuthenticationHandler>(_provider);
            await handler.InitializeAsync(
                new AuthenticationScheme(Schemes.Passkey, Schemes.Passkey, typeof(PasskeyAuthenticationHandler)),
                request);

            return (handler, request);
        }

        /// <summary>
        /// Registers a passkey for a fresh account by running the engine's attestation ceremony against
        /// <paramref name="authenticator"/>, the way the Manage page will.
        /// </summary>
        public async Task<HSUser> EnrolAsync(FakeAuthenticator authenticator)
        {
            HSUser user = await AddUserAsync("owner@example.com");
            IPasskeyHandler<HSUser> engine = _provider.GetRequiredService<IPasskeyHandler<HSUser>>();

            (_, DefaultHttpContext request) = await NewRequestAsync();

            PasskeyCreationOptionsResult creation = await engine.MakeCreationOptionsAsync(
                new PasskeyUserEntity { Id = user.Id.ToString(), Name = user.UserName!, DisplayName = user.UserName! },
                request);

            PasskeyAttestationResult attested = await engine.PerformAttestationAsync(new PasskeyAttestationContext
            {
                HttpContext = request,
                CredentialJson = authenticator.Attest(creation.CreationOptionsJson),
                AttestationState = creation.AttestationState,
            });

            attested.Succeeded.Should().BeTrue(attested.Failure?.Message);

            attested.Passkey!.Name = "fake authenticator";
            IdentityResult stored = await Users.AddOrUpdatePasskeyAsync(user, attested.Passkey);
            stored.Succeeded.Should().BeTrue();

            return user;
        }

        public async Task<HSUser> AddUserAsync(string email)
        {
            HSUser user = new(IdentityTestHarness.UsernameFor(email))
            {
                Email = email,
                EmailConfirmed = true,
            };

            IdentityResult created = await Users.CreateAsync(user, "Correct horse battery staple 1");
            created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

            return user;
        }

        public async Task<string> BodyOf(DefaultHttpContext request)
        {
            request.Response.Body.Position = 0;

            using StreamReader reader = new(request.Response.Body, Encoding.UTF8, leaveOpen: true);

            return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>The ceremony cookie a challenge set, as the browser would send it back.</summary>
        public static string CookieOf(DefaultHttpContext challenge)
        {
            string setCookie = challenge.Response.Headers.SetCookie.ToString();
            setCookie.Should().NotBeNullOrEmpty("the challenge should have started a ceremony");

            return setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
        }

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
