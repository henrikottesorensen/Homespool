using System;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Homespool.Host.Services;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Homespool.Host.Authentication;

/// <summary>
/// Authenticates <c>Authorization: Bearer hs_&lt;secret&gt;</c> against the personal access tokens in
/// <see cref="ApiTokenService"/>. Same shape as
/// <see cref="PrusaConnectPrinterAuthenticationHandler"/> — read a header, resolve a principal, fail
/// closed — but it resolves a <em>user</em> rather than a printer. Design: <c>notes/api-tokens.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The principal is built by the same factory the sign-in cookie uses</b>, so a token-authenticated
/// request is indistinguishable from a cookie-authenticated one to everything downstream:
/// <c>UserManager.GetUserAsync(User)</c>, which every <c>/api/v1</c> action calls, and the
/// <c>TeamMember.CanUse</c> checks behind <c>PrinterCommandService</c>. Hand-rolling the two claims
/// those happen to need today would work until something read a third.
/// </para>
/// <para>
/// <b>The security stamp is deliberately not validated.</b> Cookies carry one so that a password
/// change signs other sessions out; a token is not a session and is revoked by deleting its row.
/// A password change <em>does</em> invalidate tokens, but by deletion rather than by stamp — see
/// <c>ChangePassword</c> and <c>ResetPassword</c>, which revoke inside the same transaction as the
/// password write. Nothing here needs to know that, which is the point: this handler's only question
/// is whether a row exists.
/// </para>
/// </remarks>
public class ApiTokenAuthenticationHandler : AuthenticationHandler<ApiTokenAuthenticationSchemeOptions>
{
    /// <summary>
    /// Value of the <see cref="ClaimTypes.AuthenticationMethod"/> claim added to a token-authenticated
    /// identity.
    /// </summary>
    /// <remarks>
    /// <b>Its absence proves nothing.</b> A policy may accept cookie <em>and</em> token, and a request
    /// carrying both ends up with both identities merged into one principal — so this claim says "this
    /// identity came from a token", not "this request carried no cookie". Anything reasoning about
    /// CSRF eligibility has to ask about the cookie, not about this.
    /// </remarks>
    public const string AuthenticationMethod = "api_token";

    private const string BearerPrefix = "Bearer ";

    private readonly ApiTokenService _tokens;
    private readonly UserManager<HSUser> _userManager;
    private readonly IUserClaimsPrincipalFactory<HSUser> _claimsFactory;

    public ApiTokenAuthenticationHandler(ApiTokenService tokens,
                                         UserManager<HSUser> userManager,
                                         IUserClaimsPrincipalFactory<HSUser> claimsFactory,
                                         IOptionsMonitor<ApiTokenAuthenticationSchemeOptions> options,
                                         ILoggerFactory loggerFactory,
                                         UrlEncoder encoder)
        : base(options, loggerFactory, encoder)
    {
        _tokens = tokens;
        _userManager = userManager;
        _claimsFactory = claimsFactory;
    }

    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out StringValues authorization))
        {
            return AuthenticateResult.NoResult();
        }

        string header = authorization.ToString();

        // Not a Bearer token authn attempt.
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string credential = header[BearerPrefix.Length..].Trim();

        // No result rather than failure: a bearer credential without our prefix belongs to some other
        // scheme, and saying "invalid" about a credential we were never issued would be a lie that
        // also suppresses whichever handler it does belong to.
        if (!credential.StartsWith(ApiTokenService.Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        ApiToken? token = await _tokens.FindByCredentialAsync(credential, Context.RequestAborted);

        if (token is null)
        {
            // Covers a revoked token, a mistyped one and a wrong-length one alike - all
            // indistinguishable here, which is what keeps this off being an oracle. The secret is
            // never logged.
            Logger.LogInformation("API token authentication failed: no such token.");

            return AuthenticateResult.Fail("Invalid API token.");
        }

        HSUser? user = await _userManager.FindByIdAsync(token.UserId.ToString(CultureInfo.InvariantCulture));

        if (user is null)
        {
            // Structurally unreachable: deleting a user cascades to their tokens. Fail closed rather
            // than authenticate as nobody if a row is ever left inconsistent.
            Logger.LogWarning("API token {TokenId} resolves to no user.", token.Id);

            return AuthenticateResult.Fail("Invalid API token.");
        }

        ClaimsPrincipal principal = await _claimsFactory.CreateAsync(user);

        if (principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(ClaimTypes.AuthenticationMethod, AuthenticationMethod));
        }

        Logger.LogInformation("API token {TokenId} authenticated user {UserId}.", token.Id, token.UserId);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 401 with a bare <c>WWW-Authenticate: Bearer</c>, which RFC 9110 requires of a 401 and which no
    /// browser turns into a credential dialog — only <c>Basic</c> and <c>Digest</c> do that. The
    /// cookie handler's challenge, by contrast, redirects to the login page; <c>Program.cs</c> is what
    /// keeps that answer off <c>/api/</c>.
    /// </remarks>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = BearerPrefix.TrimEnd();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A plain 403: the credential was good and the answer is still no, so there is nothing to
    /// challenge for and re-presenting the same token would not help.
    /// </remarks>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    }
}
