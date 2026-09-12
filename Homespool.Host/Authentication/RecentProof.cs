using System;
using System.Globalization;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Homespool.Host.Authentication;

/// <summary>
/// The mark a person earns by proving they hold the account at <c>Account/Reauthenticate</c>, and which
/// every page carrying <see cref="RequireRecentProofAttribute"/> requires: a short, sliding window
/// during which those pages act without asking again.
/// </summary>
/// <remarks>
/// <para>
/// <b>A session is a proof of continuity, not of presence.</b> It says a browser signed in once. The
/// acts that want more - closing somebody's account, clearing a second factor, moving the address a
/// password reset goes to, removing a printer - are the acts a walk-up on an unlocked browser or a
/// holder of a stolen cookie would want, so they ask for a credential again, recently, and this is the
/// record that one was given.
/// </para>
/// <para>
/// <b>Its own timer, deliberately not the session's authentication time.</b> A login starts a session;
/// it does not open the administration screens. The window here runs from the last proof and slides
/// with use, so it is a different clock from the one the session keeps, and a person who signed in an
/// hour ago and a person who signed in a minute ago are asked the same question the first time they
/// reach a gated page. Sign-out ends it with the session.
/// </para>
/// <para>
/// <b>Bound to the account and to the server's clock, and neither is taken from the client.</b> The
/// cookie carries a data-protected user id, issue time and the method that proved; a mark earned by one
/// account does not serve another on the same browser, and the window is measured from the protected
/// timestamp rather than from the cookie's own expiry, which a client may keep as long as it likes.
/// </para>
/// <para>
/// <b>Site-wide, since the pages that want it are everywhere</b> - the administration screens, the
/// account pages, a printer's removal. What keeps it from travelling where it should not is not a path
/// but <c>SameSite=Strict</c>, <c>HttpOnly</c>, the account binding and the window.
/// </para>
/// </remarks>
public sealed class RecentProof
{
    /// <summary>The method a provider round trip is recorded as; passwords and passkeys use their handlers' constants.</summary>
    public const string ProviderMethod = "external";

    private const string CookieName = "Homespool.RecentProof";

    private const string ProtectorPurpose = "Homespool.Host.Authentication.RecentProof.v1";

    /// <summary>
    /// How long a proof lasts without activity. Long enough to work through several accounts or
    /// several settings without retyping, short enough that a walked-away browser is not an open user
    /// manager.
    /// </summary>
    /// <remarks>
    /// It <b>slides</b>: every request that finds a live proof reissues it, so the window is measured
    /// from the last gated request rather than from the credential. A fixed window would interrupt the
    /// middle of a job for no gain, since the risk being bounded is an unattended browser rather than a
    /// long session.
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;

    public RecentProof(IDataProtectionProvider dataProtection, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);

        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _time = time;
    }

    /// <summary>
    /// Whether <paramref name="userId"/> proved themselves on this browser within <paramref name="maxAge"/>
    /// - <see cref="Window"/> when none is given - sliding the window when they did.
    /// </summary>
    public bool IsProved(HttpContext context, long userId, TimeSpan? maxAge = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Cookies.TryGetValue(CookieName, out string? cookie) || string.IsNullOrEmpty(cookie))
        {
            return false;
        }

        string payload;

        try
        {
            payload = _protector.Unprotect(cookie);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Tampered, or protected by a key ring this instance no longer has. Either way it is not
            // a proof, and it is not an error worth showing anybody: the proof page is the answer to
            // both.
            return false;
        }

        string[] parts = payload.Split('|');

        if (parts.Length != 3
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long proved)
            || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long issuedTicks))
        {
            return false;
        }

        if (proved != userId)
        {
            return false;
        }

        DateTimeOffset issued = new(issuedTicks, TimeSpan.Zero);
        TimeSpan age = _time.GetUtcNow() - issued;

        if (age < TimeSpan.Zero || age > (maxAge ?? Window))
        {
            return false;
        }

        // Live, so the window starts again from now, with the method it was earned by.
        Grant(context, userId, parts[2]);

        return true;
    }

    /// <summary>Records that <paramref name="userId"/> proved themselves by <paramref name="method"/> on this browser, for <see cref="Window"/>.</summary>
    public void Grant(HttpContext context, long userId, string method)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(method);

        DateTimeOffset now = _time.GetUtcNow();

        string payload = string.Create(CultureInfo.InvariantCulture, $"{userId}|{now.UtcTicks}|{method.Replace('|', '_')}");

        context.Response.Cookies.Append(CookieName, _protector.Protect(payload), Options(context, now.Add(Window)));
    }

    /// <summary>
    /// Ends any proof on this browser. Sign-out calls it, so that signing out and back in inside the
    /// window does not walk straight past the gate.
    /// </summary>
    public void Clear(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Cookies.Delete(CookieName, Options(context, expires: null));
    }

    private static CookieOptions Options(HttpContext context, DateTimeOffset? expires)
    {
        return new CookieOptions
        {
            Path = "/",
            HttpOnly = true,

            // Strict rather than Lax: a cross-site navigation that arrived proved would be exactly the
            // walked-away-browser case this is bounding, and the cost of Strict is one more proof on a
            // gated page reached from somewhere else, which is rare and cheap.
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Expires = expires,
        };
    }
}
