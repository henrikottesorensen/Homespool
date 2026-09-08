using System;
using System.Globalization;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Homespool.Host.Authentication;

/// <summary>
/// The mark an administrator earns by proving themselves at <c>Admin/Challenge</c>, and which every
/// page under <c>/Admin</c> requires: a short, sliding window during which the administration screens
/// act without asking again.
/// </summary>
/// <remarks>
/// <para>
/// <b>A window rather than a proof per act.</b> The four account pages ask for the password at the
/// act itself, which is right for them - each is one irreversible thing done rarely. Administration is
/// a session of work: closing an account, reading its tokens, lifting a lockout, moving on to the
/// next person. A field beside every button there is retyped so often it stops being read, and on a
/// page about somebody else it asks a question with two plausible answers - whose password is this,
/// mine or theirs.
/// </para>
/// <para>
/// <b>What it is not.</b> It is not authentication and it is not authorisation: the session cookie
/// still says who this is, <c>[Authorize(Roles = Admin)]</c> still says they may be here, and this
/// says only that the person at the keyboard proved it recently. All three have to hold, and this
/// one is checked last.
/// </para>
/// <para>
/// <b>Bound to the account and to the clock, and neither is taken from the client.</b> The cookie
/// carries a data-protected user id and issue time; a cookie earned by one account does not elevate
/// another on the same browser, and the window is measured from the protected timestamp rather than
/// from the cookie's own expiry, which a client may keep as long as it likes.
/// </para>
/// <para>
/// <b>Scoped to <c>/Admin</c> by path</b>, so it is not sent with a request to any other part of the
/// application — a credential that travels no further than the pages it is for.
/// </para>
/// </remarks>
public sealed class AdminElevation
{
    /// <summary>The path the cookie is scoped to, which is also the surface the gate covers.</summary>
    public const string Path = "/Admin";

    private const string CookieName = "Homespool.AdminElevation";

    private const string ProtectorPurpose = "Homespool.Host.Authentication.AdminElevation.v1";

    /// <summary>
    /// How long an elevation lasts without activity. Long enough to work through several accounts
    /// without retyping, short enough that a walked-away browser is not an open user manager.
    /// </summary>
    /// <remarks>
    /// It <b>slides</b>: every request that finds a live elevation reissues it, so the window is
    /// measured from the last administration request rather than from the password. A fixed window
    /// would interrupt the middle of a job for no gain, since the risk being bounded is an unattended
    /// browser rather than a long session.
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;

    public AdminElevation(IDataProtectionProvider dataProtection, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);

        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _time = time;
    }

    /// <summary>
    /// Whether <paramref name="userId"/> is elevated on this request, sliding the window when they
    /// are.
    /// </summary>
    public bool IsElevated(HttpContext context, long userId)
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
            // an elevation, and it is not an error worth showing anybody: the challenge page is the
            // answer to both.
            return false;
        }

        string[] parts = payload.Split('|');

        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long elevated)
            || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long issuedTicks))
        {
            return false;
        }

        if (elevated != userId)
        {
            return false;
        }

        DateTimeOffset issued = new(issuedTicks, TimeSpan.Zero);

        if (_time.GetUtcNow() - issued > Window)
        {
            return false;
        }

        // Live, so the window starts again from now.
        Grant(context, userId);

        return true;
    }

    /// <summary>Elevates <paramref name="userId"/> on this browser for <see cref="Window"/>.</summary>
    public void Grant(HttpContext context, long userId)
    {
        ArgumentNullException.ThrowIfNull(context);

        DateTimeOffset now = _time.GetUtcNow();

        string payload = string.Create(CultureInfo.InvariantCulture, $"{userId}|{now.UtcTicks}");

        context.Response.Cookies.Append(CookieName, _protector.Protect(payload), Options(context, now.Add(Window)));
    }

    /// <summary>
    /// Ends any elevation on this browser. Sign-out calls it, so that signing out and back in inside
    /// ten minutes does not walk straight into the administration screens.
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
            Path = Path,
            HttpOnly = true,

            // Strict rather than Lax: nothing links into the administration screens from anywhere
            // else, so there is no navigation this breaks, and a cross-site GET that arrived
            // elevated would be exactly the walked-away-browser case this is bounding.
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Expires = expires,
        };
    }
}
