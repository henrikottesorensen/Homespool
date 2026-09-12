using System;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Homespool.Host.Authentication;

/// <summary>
/// Acts on <see cref="RequireRecentProofAttribute"/>: a handler the page or the method declared runs
/// only for a request carrying a live <see cref="RecentProof"/> for the signed-in account, and everyone
/// else is sent to <c>Account/Reauthenticate</c> with the page's own path to come back to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered once, for every page, and inert where nothing is declared.</b> A page filter is
/// discovered from the page model's attributes, never from a handler method's, so an attribute that
/// wanted to gate one handler could not be its own filter. This one is global and reads both places;
/// what it costs is one reflection lookup per request, and what it buys is that a mixed page - a
/// printer's detail, a token list - can gate the one act that destroys something and leave the rest
/// alone.
/// </para>
/// <para>
/// <b>The path goes back, not the act.</b> <c>returnUrl</c> is the request path, so the person returns
/// to the page and presses the button again themselves. See the attribute for why.
/// </para>
/// </remarks>
public sealed class RecentProofPageFilter : IAsyncPageFilter
{
    /// <summary>Where a request without a live proof is sent.</summary>
    public const string ReauthenticatePage = "/Account/Reauthenticate";

    private readonly RecentProof _proof;

    public RecentProofPageFilter(RecentProof proof)
    {
        _proof = proof;
    }

    /// <inheritdoc/>
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (Declared(context) is not { } required)
        {
            await next();

            return;
        }

        string? subject = context.HttpContext.User.FindFirst(JwtClaimTypes.Subject)?.Value;

        if (subject is not null
            && long.TryParse(subject, NumberStyles.None, CultureInfo.InvariantCulture, out long userId)
            && _proof.IsProved(context.HttpContext, userId, required.MaxAge))
        {
            await next();

            return;
        }

        context.Result = new RedirectToPageResult(ReauthenticatePage, new { returnUrl = context.HttpContext.Request.Path.Value });
    }

    /// <summary>
    /// The declaration that applies to this handler: the method's own first, then the page model's.
    /// <see langword="null"/> when neither says anything, which is every page that never asked.
    /// </summary>
    private static RequireRecentProofAttribute? Declared(PageHandlerExecutingContext context)
    {
        return context.HandlerMethod?.MethodInfo.GetCustomAttribute<RequireRecentProofAttribute>(inherit: true)
               ?? context.HandlerInstance.GetType().GetCustomAttribute<RequireRecentProofAttribute>(inherit: true);
    }
}
