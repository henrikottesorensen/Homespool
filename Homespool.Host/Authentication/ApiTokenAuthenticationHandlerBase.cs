using System;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// Resolves a personal access token to its owner. Everything except <em>which header the credential
/// arrived in</em> lives here; a subclass supplies only that. Design: <c>notes/api-tokens.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is one kind of token, presented two ways.</b> The split into schemes is about which
/// surface accepts which header, not about two credentials - so the lookup, the fail-closed branches
/// and the claim below are shared rather than reimplemented. Two copies of a security branch drift,
/// and the half that goes stale is not announced.
/// </para>
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
public abstract class ApiTokenAuthenticationHandlerBase : AuthenticationHandler<ApiTokenAuthenticationSchemeOptions>
{
    /// <summary>A token presented the native way, in <c>Authorization: Bearer</c>.</summary>
    public const string BearerAuthenticationMethod = "api_token";

    /// <summary>The same token presented in <c>X-Api-Key</c>.</summary>
    public const string ApiKeyAuthenticationMethod = "x_api_key";

    private readonly ApiTokenService _tokens;
    private readonly UserManager<HSUser> _userManager;
    private readonly IUserClaimsPrincipalFactory<HSUser> _claimsFactory;

    protected ApiTokenAuthenticationHandlerBase(ApiTokenService tokens,
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

    /// <summary>
    /// What this scheme names in <c>WWW-Authenticate</c> on a 401, or <see langword="null"/> for a
    /// scheme with nothing to name.
    /// </summary>
    /// <remarks>
    /// RFC 9110 requires a challenge on a 401, and there is no registered scheme meaning "put it in a
    /// header of my own" — so a scheme that reads one answers <see langword="null"/> rather than
    /// claiming to accept something it does not. A policy combining both schemes still produces a
    /// complete 401, because the bearer scheme in it names <c>Bearer</c>.
    /// </remarks>
    protected abstract string? ChallengeScheme { get; }

    /// <summary>
    /// What this scheme writes as the <see cref="ClaimTypes.AuthenticationMethod"/> claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Distinct per scheme, deliberately.</b> One shared value would have been defensible - it is one
    /// kind of token either way - but it throws away the only provenance the principal carries, in the
    /// claim whose entire job is to record how it was authenticated. <c>diagnostics.md</c>'s finding is
    /// that this application has no correlation handles at all; spending one it already emits would be
    /// the wrong direction, and keeping it costs nothing.
    /// </para>
    /// <para>
    /// <b>Do not read it as "is this a token".</b> The values are two, so an equality test against one
    /// of them silently excludes the other - and its <em>absence</em> proves nothing anyway, since a
    /// policy may accept cookie and token both, and a request carrying both ends up with the identities
    /// merged into one principal. The question "which credentials could have got here" is answered by
    /// the policy's scheme list, which is declarative and cannot drift.
    /// </para>
    /// </remarks>
    protected abstract string AuthenticationMethod { get; }

    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? credential = ReadCredential();

        // This scheme's header is absent, or holds something that is not a credential at all.
        if (credential is null)
        {
            return AuthenticateResult.NoResult();
        }

        // No result rather than failure: a credential without our prefix belongs to some other
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

    /// <summary>
    /// The credential this request presents to <em>this</em> scheme, or <see langword="null"/> when it
    /// presents none.
    /// </summary>
    /// <remarks>
    /// Reads one header and strips whatever decorates it. Whether the result is a token of ours is not
    /// this method's question — <see cref="HandleAuthenticateAsync"/> owns the <c>hs_</c> check, so
    /// every scheme judges a credential identically.
    /// </remarks>
    protected abstract string? ReadCredential();

    /// <inheritdoc/>
    /// <remarks>
    /// 401 with the challenge <see cref="ChallengeScheme"/> names, if any. No browser turns either
    /// answer into a credential dialog — only <c>Basic</c> and <c>Digest</c> do that. The cookie
    /// handler's challenge, by contrast, redirects to the login page; <c>Program.cs</c> is what keeps
    /// that answer off <c>/api/</c>.
    /// </remarks>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;

        if (ChallengeScheme is not null)
        {
            Response.Headers.WWWAuthenticate = ChallengeScheme;
        }

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
