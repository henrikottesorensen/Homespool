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
/// The bearer-token authentication handler: which credentials it claims, which it refuses, and what
/// it answers when there is none.
/// </summary>
/// <remarks>
/// <b>The rejections are the tests that matter.</b> A handler that authenticated everything would pass
/// the positive case and every route test in the suite, so the negatives below were each checked by
/// mutation - removing the prefix check, the lookup, or the fail-closed branch makes exactly the
/// matching test go red.
/// </remarks>
public sealed class ApiTokenAuthenticationHandlerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-apiauth-{Guid.NewGuid():N}.db");

    private static async Task<HSUser> AddUserAsync(HSDbContext context, string email = "owner@example.com")
    {
        HSUser user = new(email)
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();

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
        await context.Database.MigrateAsync();

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

    /// <summary>
    /// Builds the handler over a real Identity stack, since the principal it returns is built by
    /// Identity's own claims factory - the point of which is that a token-authenticated request looks
    /// exactly like a cookie-authenticated one downstream.
    /// </summary>
    private static async Task<(ApiTokenAuthenticationHandler handler, DefaultHttpContext httpContext)> NewHandlerAsync(
        HSDbContext context, string? authorization)
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, IServiceProvider provider) =
            IdentityTestHarness.BuildIdentityServices(context);

        if (authorization is not null)
        {
            httpContext.Request.Headers.Authorization = authorization;
        }

        ApiTokenAuthenticationHandler handler = new(
            new ApiTokenService(context),
            users,
            provider.GetRequiredService<IUserClaimsPrincipalFactory<HSUser>>(),
            new StaticOptionsMonitor(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default);

        await handler.InitializeAsync(
            new AuthenticationScheme(Schemes.ApiToken, Schemes.ApiToken, typeof(ApiTokenAuthenticationHandler)),
            httpContext);

        return (handler, httpContext);
    }

    // ---------- credentials this scheme does not claim ----------

    /// <summary>
    /// No <c>Authorization</c> header at all is not this scheme's business: NoResult, not a failure, so
    /// the cookie scheme sharing the API policy still gets its turn.
    /// </summary>
    [Fact]
    public async Task ARequestWithoutAnAuthorizationHeaderYieldsNoResult()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (ApiTokenAuthenticationHandler handler, _) = await NewHandlerAsync(context, authorization: null);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.None.Should().BeTrue();
    }

    /// <summary>
    /// A credential belonging to another scheme is left alone. Failing on it would be a lie about a
    /// credential we never issued, and would suppress whichever handler it does belong to.
    /// </summary>
    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc")]
    public async Task ACredentialOfAnotherSchemeYieldsNoResult(string authorization)
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (ApiTokenAuthenticationHandler handler, _) = await NewHandlerAsync(context, authorization);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.None.Should().BeTrue();
        result.Failure.Should().BeNull();
    }

    // ---------- credentials it claims and refuses ----------

    /// <summary>
    /// A credential shaped like ours but matching no row fails outright - it is not "somebody else's
    /// problem", because the prefix says it was meant to be one of ours.
    /// </summary>
    [Fact]
    public async Task AnUnknownTokenFails()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (ApiTokenAuthenticationHandler handler, _) = await NewHandlerAsync(
            context, $"Bearer {ApiTokenService.Prefix}{new string('A', ApiTokenService.SecretLength)}");

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse();
        result.None.Should().BeFalse("the prefix claims it for this scheme");
        result.Failure.Should().NotBeNull();
    }

    /// <summary>Revoking a token stops the very next request that presents it.</summary>
    [Fact]
    public async Task ARevokedTokenFails()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService tokens = new(context);

        (ApiToken token, string plaintext) = await tokens.CreateAsync(user.Id, "laptop", CancellationToken.None);
        await tokens.RevokeAsync(user.Id, token.Id, CancellationToken.None);

        (ApiTokenAuthenticationHandler handler, _) = await NewHandlerAsync(context, $"Bearer {plaintext}");

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    // ---------- the credential it accepts ----------

    /// <summary>
    /// A good token authenticates as its owner, and the principal carries what
    /// <c>UserManager.GetUserAsync</c> reads - the claim every <c>/api/v1</c> action depends on to know
    /// who is calling.
    /// </summary>
    [Fact]
    public async Task AGoodTokenAuthenticatesAsItsOwner()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService tokens = new(context);

        (_, string plaintext) = await tokens.CreateAsync(user.Id, "laptop", CancellationToken.None);

        (ApiTokenAuthenticationHandler handler, _) = await NewHandlerAsync(context, $"Bearer {plaintext}");

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
        result.Principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(user.Id.ToString());
        result.Principal.FindFirstValue(ClaimTypes.AuthenticationMethod)
              .Should().Be(ApiTokenAuthenticationHandler.AuthenticationMethod);
    }

    /// <summary>The scheme name is case-insensitive on the wire, as RFC 9110 says it is.</summary>
    [Fact]
    public async Task TheBearerKeywordIsCaseInsensitive()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService tokens = new(context);

        (_, string plaintext) = await tokens.CreateAsync(user.Id, "laptop", CancellationToken.None);

        (ApiTokenAuthenticationHandler handler, _) = await NewHandlerAsync(context, $"bearer {plaintext}");

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    // ---------- what it answers when there is no credential ----------

    /// <summary>
    /// 401 with a bare <c>WWW-Authenticate: Bearer</c> - what RFC 9110 requires of a 401, and what no
    /// browser turns into a credential dialog (only <c>Basic</c> and <c>Digest</c> do that).
    /// </summary>
    [Fact]
    public async Task ChallengeAnswers401WithABearerHeader()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (ApiTokenAuthenticationHandler handler, DefaultHttpContext httpContext) = await NewHandlerAsync(context, authorization: null);

        // Act
        await handler.ChallengeAsync(properties: null);

        // Assert
        httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        httpContext.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
    }

    /// <summary>
    /// 403 rather than another 401: the caller was identified and the answer is still no, so
    /// re-presenting the same token would not help.
    /// </summary>
    [Fact]
    public async Task ForbidAnswers403()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (ApiTokenAuthenticationHandler handler, DefaultHttpContext httpContext) = await NewHandlerAsync(context, authorization: null);

        // Act
        await handler.ForbidAsync(properties: null);

        // Assert
        httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        httpContext.Response.Headers.WWWAuthenticate.ToString().Should().BeEmpty("there is nothing to challenge for");
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
