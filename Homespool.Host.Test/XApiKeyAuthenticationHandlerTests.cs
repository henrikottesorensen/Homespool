using System;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The <c>X-Api-Key</c> authentication scheme: the same personal access token in the only header
/// PrusaSlicer's print-host client sends.
/// </summary>
/// <remarks>
/// <b>The two tests that matter are the boundaries</b>, not the happy path. That this scheme refuses a
/// bearer credential is what keeps <c>Policies.Api</c> and <c>Policies.Compat</c> genuinely
/// different surfaces, and that it authenticates as the plain owner is what stops it becoming a
/// second, weaker kind of credential. Both were checked by mutation: reading the other header, or
/// dropping the <c>hs_</c> prefix check, makes exactly the matching test go red.
/// </remarks>
public sealed class XApiKeyAuthenticationHandlerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-xapikey-{Guid.NewGuid():N}.db");

    private static async Task<HSUser> AddUserAsync(HSDbContext context, string email = "owner@example.com")
    {
        HSUser user = new(email)
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private HSDbContext NewContext()
    {
        DbContextOptions<HSDbContext> options = new DbContextOptionsBuilder<HSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new HSDbContext(options);
    }

    private async Task<HSDbContext> MigratedContextAsync()
    {
        HSDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

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

    private static async Task<(XApiKeyAuthenticationHandler handler, DefaultHttpContext httpContext)> NewHandlerAsync(
        HSDbContext context, string? apiKey, string? authorization = null)
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, IServiceProvider provider) =
            IdentityTestHarness.BuildIdentityServices(context);

        if (apiKey is not null)
        {
            httpContext.Request.Headers[XApiKeyAuthenticationHandler.HeaderName] = apiKey;
        }

        if (authorization is not null)
        {
            httpContext.Request.Headers.Authorization = authorization;
        }

        XApiKeyAuthenticationHandler handler = new(
            new ApiTokenService(context),
            users,
            provider.GetRequiredService<IUserClaimsPrincipalFactory<HSUser>>(),
            new StaticOptionsMonitor(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default);

        await handler.InitializeAsync(
            new AuthenticationScheme(Schemes.XApiKey, Schemes.XApiKey, typeof(XApiKeyAuthenticationHandler)),
            httpContext);

        return (handler, httpContext);
    }

    // ---------- credentials this scheme does not claim ----------

    /// <summary>No header of ours is not this scheme's business: NoResult, not a failure.</summary>
    [Fact]
    public async Task ARequestWithoutTheHeaderYieldsNoResult()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (XApiKeyAuthenticationHandler handler, _) = await NewHandlerAsync(context, apiKey: null);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.None.Should().BeTrue();
    }

    /// <summary>
    /// Somebody else's API key is not ours to judge. The <c>hs_</c> prefix is the single thing that
    /// decides - and a genuine OctoPrint key is the case that actually arises, the header being
    /// OctoPrint's, so this is not a hypothetical.
    /// </summary>
    [Fact]
    public async Task AKeyWithoutOurPrefixYieldsNoResult()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (XApiKeyAuthenticationHandler handler, _) = await NewHandlerAsync(
            context, apiKey: "0123456789ABCDEF0123456789ABCDEF");

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.None.Should().BeTrue();
        result.Failure.Should().BeNull();
    }

    /// <summary>
    /// A perfectly good token in <c>Authorization: Bearer</c> is the bearer scheme's, not this one's.
    /// </summary>
    /// <remarks>
    /// The mirror of the same test on the bearer handler, and the pair is what makes the schemes real:
    /// each reads one header, so a policy naming one of them grants exactly one way in.
    /// </remarks>
    [Fact]
    public async Task ABearerTokenIsNotThisSchemesBusiness()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService tokens = new(context);

        (_, string plaintext) = await tokens.CreateAsync(user.Id, "laptop", CancellationToken.None);

        (XApiKeyAuthenticationHandler handler, _) = await NewHandlerAsync(
            context, apiKey: null, authorization: $"Bearer {plaintext}");

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.None.Should().BeTrue();
        result.Failure.Should().BeNull();
    }

    // ---------- credentials it claims and refuses ----------

    /// <summary>
    /// A key shaped like ours but matching no row fails outright - the prefix says it was meant to be
    /// one of ours, so "somebody else's problem" would be the wrong answer.
    /// </summary>
    [Fact]
    public async Task AnUnknownTokenFails()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (XApiKeyAuthenticationHandler handler, _) = await NewHandlerAsync(
            context, apiKey: $"{ApiTokenService.Prefix}{new string('A', ApiTokenService.SecretLength)}");

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse();
        result.None.Should().BeFalse("the prefix claims it for this scheme");
        result.Failure.Should().NotBeNull();
    }

    /// <summary>Revoking a token stops the slicer's very next upload, as it stops every other caller.</summary>
    [Fact]
    public async Task ARevokedTokenFails()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService tokens = new(context);

        (ApiToken token, string plaintext) = await tokens.CreateAsync(user.Id, "slicer", CancellationToken.None);
        await tokens.RevokeAsync(user.Id, token.Id, CancellationToken.None);

        (XApiKeyAuthenticationHandler handler, _) = await NewHandlerAsync(context, apiKey: plaintext);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    // ---------- the credential it accepts ----------

    /// <summary>
    /// A good token authenticates as its owner, carrying the same claims a bearer one does - except the
    /// authentication method, which records <em>this</em> scheme.
    /// </summary>
    /// <remarks>
    /// The distinct value is the point: it is the only provenance the principal carries, and a shared
    /// one would leave nothing able to tell afterwards which header a request arrived on.
    /// </remarks>
    [Fact]
    public async Task AGoodTokenAuthenticatesAsItsOwner()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService tokens = new(context);

        (_, string plaintext) = await tokens.CreateAsync(user.Id, "slicer", CancellationToken.None);

        (XApiKeyAuthenticationHandler handler, _) = await NewHandlerAsync(context, apiKey: plaintext);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
        result.Principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(user.Id.ToString());
        result.Principal.FindFirstValue(ClaimTypes.AuthenticationMethod)
              .Should().Be(ApiTokenAuthenticationHandlerBase.ApiKeyAuthenticationMethod);
        result.Principal.FindFirstValue(ClaimTypes.AuthenticationMethod)
              .Should().NotBe(ApiTokenAuthenticationHandlerBase.BearerAuthenticationMethod,
                              "the two schemes must stay tellable apart after the fact");
    }

    /// <summary>
    /// Surrounding whitespace is stripped, which is what a value pasted out of a token page tends to
    /// carry.
    /// </summary>
    [Fact]
    public async Task ThePastedValueIsTrimmed()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService tokens = new(context);

        (_, string plaintext) = await tokens.CreateAsync(user.Id, "slicer", CancellationToken.None);

        (XApiKeyAuthenticationHandler handler, _) = await NewHandlerAsync(context, apiKey: $" {plaintext} ");

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    // ---------- what it answers when there is no credential ----------

    /// <summary>
    /// 401, and <b>no</b> <c>WWW-Authenticate</c>: there is no registered challenge scheme meaning "put
    /// it in a header of my own", and naming <c>Bearer</c> would advertise a credential this scheme does
    /// not read. A policy pairing it with the bearer scheme still answers with a complete challenge,
    /// because that scheme supplies one.
    /// </summary>
    [Fact]
    public async Task ChallengeAnswers401WithoutNamingAScheme()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (XApiKeyAuthenticationHandler handler, DefaultHttpContext httpContext) = await NewHandlerAsync(context, apiKey: null);

        // Act
        await handler.ChallengeAsync(properties: null);

        // Assert
        httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        httpContext.Response.Headers.WWWAuthenticate.ToString().Should().BeEmpty();
    }

    /// <summary>
    /// 403 rather than another 401: the credential was good and the answer is still no, so
    /// re-presenting the same token would not help.
    /// </summary>
    [Fact]
    public async Task ForbidAnswers403()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (XApiKeyAuthenticationHandler handler, DefaultHttpContext httpContext) = await NewHandlerAsync(context, apiKey: null);

        // Act
        await handler.ForbidAsync(properties: null);

        // Assert
        httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<ApiTokenAuthenticationSchemeOptions>
    {
        private readonly ApiTokenAuthenticationSchemeOptions _options = new();

        public ApiTokenAuthenticationSchemeOptions CurrentValue => _options;

        public ApiTokenAuthenticationSchemeOptions Get(string? name)
        {
            return _options;
        }

        public IDisposable? OnChange(Action<ApiTokenAuthenticationSchemeOptions, string?> listener)
        {
            return null;
        }
    }
}
