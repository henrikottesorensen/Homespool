using System;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// The check on the application cookie, made on every request: the cookie signs somebody in only while
/// the <see cref="UserSession"/> it names is live.
/// </summary>
/// <remarks>
/// <para>
/// <b>One query, every request, and nothing else.</b> It compares the account's stamp and looks for the
/// session's passkey as well as the row, so a password change, a closed account, a revoked passkey or
/// a revoked session ends the session on the browser's next request. The principal is not rebuilt from
/// the account on a timer, as the framework's stamp validator does: every change a person makes to
/// their own claims re-issues the cookie through <see cref="LocalSignIn.RefreshSignInAsync"/>, and every
/// change made to somebody else's account moves its stamp, which ends its sessions outright. A change to
/// another account's claims that did neither would not reach its cookie until the next sign-in - so
/// such a change must move the stamp.
/// </para>
/// <para>
/// <b>A dead session ends everything on the browser</b>: its row, the application, external and pending
/// cookies, and the remembered browser - as the framework's stamp validator does on a mismatch, since
/// a dead session means the account changed underneath this browser or somebody ended it on purpose.
/// </para>
/// </remarks>
public sealed class SessionStampValidator : ISecurityStampValidator
{
    private readonly UserManager<HSUser> _users;
    private readonly UserSessionService _sessions;
    private readonly LocalSignIn _signIn;
    private readonly ILogger<SessionStampValidator> _logger;

    public SessionStampValidator(UserManager<HSUser> users,
                                 UserSessionService sessions,
                                 LocalSignIn signIn,
                                 ILogger<SessionStampValidator> logger)
    {
        _users = users;
        _sessions = sessions;
        _signIn = signIn;
        _logger = logger;
    }

    /// <summary>
    /// The application cookie's <c>OnCheckSlidingExpiration</c>: when the handler is about to renew the
    /// cookie, the session's row is moved to the renewed cookie's expiry, so that the sweep never
    /// removes a session a cookie still carries. Once per half of the cookie's lifetime of use, and
    /// only for a live row.
    /// </summary>
    public static async Task CheckSlidingExpirationAsync(CookieSlidingExpirationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? secret = context.Principal?.FindFirstValue(HSClaimTypes.SessionSecret);

        if (!context.ShouldRenew || secret is null || context.Properties is not { IssuedUtc: { } issued, ExpiresUtc: { } expires })
        {
            return;
        }

        // What the handler will write: its own clock's now, plus the ticket's length.
        DateTimeOffset now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();

        await context.HttpContext
                     .RequestServices
                     .GetRequiredService<UserSessionService>()
                     .ExtendAsync(secret, now + (expires - issued), context.HttpContext.RequestAborted);
    }

    /// <inheritdoc/>
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ClaimsPrincipal? principal = context.Principal;
        string? userId = principal is null ? null : _users.GetUserId(principal);
        string? secret = principal?.FindFirstValue(HSClaimTypes.SessionSecret);

        bool live = userId is not null &&
                    secret is not null &&
                    long.TryParse(userId, out long id) &&
                    await _sessions.IsLiveAsync(id, secret, context.HttpContext.RequestAborted);

        if (live)
        {
            return;
        }

        _logger.LogDebug("Session validation failed; rejecting the cookie and ending the session.");

        if (secret is not null)
        {
            await _sessions.EndAsync(secret, context.HttpContext.RequestAborted);
        }

        context.RejectPrincipal();
        await _signIn.SignOutAsync(context.HttpContext);
        await context.HttpContext.SignOutAsync(IdentityConstants.TwoFactorRememberMeScheme);
    }
}
