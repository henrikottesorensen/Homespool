using System;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Homespool.Host.Authentication;

/// <summary>
/// Makes the sign-in cookie answer <c>/api</c> requests with status codes instead of redirects.
/// </summary>
/// <remarks>
/// <para>
/// The cookie handler's whole job is to send a browser somewhere it can fix the problem: a challenge
/// redirects to the login page, a forbid to the access-denied page. For a script that is the wrong
/// answer twice over — an HTML page arrives where JSON was expected, and it arrives as <c>200</c>, so
/// a caller checking the status code sees success. A personal access token makes scripted callers the
/// normal case rather than a curiosity, which is what makes this worth fixing now.
/// </para>
/// <para>
/// <b>Scoped to the path, not to the credential.</b> Both answers are decided by where the request was
/// going, so a browser hitting <c>/api/v1</c> directly gets the status code too. That is the honest
/// reading: the endpoint is an API whoever is calling it, and the alternative — sniffing
/// <c>Accept</c> or the presence of a bearer header — makes the response depend on how the caller
/// asked rather than on what it asked for.
/// </para>
/// </remarks>
public static class ApiStatusCodeCookieEvents
{
    /// <summary>The prefix these responses apply to. Everything navigable lives outside it.</summary>
    public const string ApiPathPrefix = "/api";

    /// <summary>
    /// Replaces the challenge and forbid redirects with <c>401</c> and <c>403</c> for
    /// <see cref="ApiPathPrefix"/> requests, leaving every other path redirecting exactly as before.
    /// </summary>
    public static void Apply(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The default implementations are captured and called for non-API paths rather than
        // reimplemented, so the login and access-denied behaviour stays whatever the framework says it
        // is - including the returnUrl handling in the redirect URI, which is easy to get subtly wrong.
        Func<RedirectContext<CookieAuthenticationOptions>, Task> redirectToLogin = options.Events.OnRedirectToLogin;
        Func<RedirectContext<CookieAuthenticationOptions>, Task> redirectToAccessDenied = options.Events.OnRedirectToAccessDenied;

        options.Events.OnRedirectToLogin = context =>
        {
            if (IsApiRequest(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                return Task.CompletedTask;
            }

            return redirectToLogin(context);
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (IsApiRequest(context.Request))
            {
                // 403 rather than 401: the caller was identified and the answer is still no, so there
                // is nothing to re-present. Same distinction the token handler draws.
                context.Response.StatusCode = StatusCodes.Status403Forbidden;

                return Task.CompletedTask;
            }

            return redirectToAccessDenied(context);
        };
    }

    private static bool IsApiRequest(HttpRequest request)
    {
        return request.Path.StartsWithSegments(ApiPathPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
