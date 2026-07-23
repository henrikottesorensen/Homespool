using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PrinterService.Host.E2ETest;

/// <summary>
/// Whether a response actually signed someone in, for tests that assert a rejected request must
/// not have. A <c>Set-Cookie</c> header is present on <em>every</em> response regardless of outcome
/// (the antiforgery cookie refreshes per-request), so this checks for the specific Identity
/// application cookie by name rather than "any cookie at all" - the same check
/// <see cref="LoginFlowTests"/> and <see cref="LoginWith2faTests"/> both need.
/// </summary>
public static class IdentityCookieTestHelper
{
    public static bool SetTheApplicationCookie(IServiceProvider services, HttpResponseMessage response)
    {
        using IServiceScope scope = services.CreateScope();
        CookieAuthenticationOptions cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        return response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies)
               && cookies.Any(c => c.StartsWith($"{cookieOptions.Cookie.Name}=", StringComparison.Ordinal));
    }
}
