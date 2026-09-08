using System;
using System.Globalization;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Homespool.Host.Authentication;

/// <summary>
/// Requires a live <see cref="AdminElevation"/> on the page it is applied to, sending a
/// administrator who has not proved themselves recently to <c>Admin/Challenge</c> first.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared on the page rather than applied to the folder</b>, for the reason <c>Program</c>
/// gives for declining an <c>AuthorizeFolder</c> convention: somebody auditing one page should see
/// what protects it by looking at it. What keeps that from being a page somebody forgets is
/// <c>AdminElevationDeclarationTests</c>, which reflects over every page under <c>Pages/Admin</c>
/// and requires this attribute or an explicit <see cref="NoAdminElevationAttribute"/>.
/// </para>
/// <para>
/// <b>It runs after authorisation, never instead of it.</b> Authorisation filters run first, so an
/// anonymous visitor is at the login page and a signed-in non-administrator is refused before this
/// is reached. Every page carrying this also carries <c>[Authorize(Roles = AdminBootstrap.AdminRole)]</c>.
/// </para>
/// <para>
/// <b>A refused POST is not replayed after the challenge.</b> The redirect goes to the page's own
/// path, so an act attempted on a stale tab has to be asked for again deliberately. Re-running a
/// destructive act automatically once a password arrives is how a walked-away browser turns into a
/// surprise.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequireAdminElevationAttribute : Attribute, IAsyncPageFilter
{
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

        AdminElevation elevation = context.HttpContext.RequestServices.GetRequiredService<AdminElevation>();

        string? subject = context.HttpContext.User.FindFirst(JwtClaimTypes.Subject)?.Value;

        if (subject is null
            || !long.TryParse(subject, NumberStyles.None, CultureInfo.InvariantCulture, out long userId)
            || !elevation.IsElevated(context.HttpContext, userId))
        {
            context.Result = new RedirectToPageResult(
                "/Admin/Challenge",
                new { returnUrl = context.HttpContext.Request.Path.Value });

            return;
        }

        await next();
    }
}
