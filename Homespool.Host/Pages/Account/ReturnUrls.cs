using Microsoft.AspNetCore.Mvc;

namespace Homespool.Host.Pages.Account;

/// <summary>
/// Where a sign-in page sends the person afterwards. <c>LocalRedirect</c> throws for anything off this
/// site, and by the time these pages redirect the session row is already written and a recovery code
/// already spent, so a bad <c>returnUrl</c> has to be dealt with before then rather than at the redirect.
/// </summary>
public static class ReturnUrls
{
    /// <summary>
    /// <paramref name="returnUrl"/> if it is somewhere on this site, and the home page otherwise,
    /// including when there is none. The sign-in has succeeded by now; where the person was headed is
    /// not a reason to refuse it.
    /// </summary>
    public static string LocalOrHome(this IUrlHelper url, string? returnUrl)
    {
        return returnUrl is not null && url.IsLocalUrl(returnUrl) ? returnUrl : url.Content("~/");
    }
}
