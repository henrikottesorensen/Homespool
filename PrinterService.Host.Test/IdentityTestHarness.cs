using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using PrinterService.Data;
using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Test;

/// <summary>
/// An <see cref="IEmailSender"/> that records what it was asked to send instead of sending it, so a
/// test can assert on the recipient/subject/body without any real SMTP.
/// </summary>
internal sealed class CapturingEmailSender : IEmailSender
{
    public List<(string Email, string Subject, string HtmlMessage)> SentEmails { get; } = [];

    /// <summary>What <see cref="SendEmailAsync"/> reports back to the caller. <see cref="EmailSendResult.Sent"/> by default.</summary>
    public EmailSendResult Result { get; set; } = EmailSendResult.Sent;

    public Task<EmailSendResult> SendEmailAsync(string email, string subject, string htmlMessage)
    {
        SentEmails.Add((email, subject, htmlMessage));

        return Task.FromResult(Result);
    }
}

/// <summary>
/// Builds a real <see cref="UserManager{TUser}"/>/<see cref="SignInManager{TUser}"/> against a migrated
/// <see cref="PSDbContext"/>, plus a minimal <see cref="IUrlHelper"/>, so <c>RegisterModel</c> and
/// <c>Admin/Invites/CreateModel</c> can be unit tested by calling their <c>OnGetAsync</c>/<c>OnPostAsync</c>
/// directly - the documented approach for unit testing Razor PageModels - without a
/// TestServer/WebApplicationFactory.
/// </summary>
internal static class IdentityTestHarness
{
    /// <summary>
    /// Wires up Identity against <paramref name="context"/> (the same tracked instance the test asserts
    /// against) and returns the pieces a PageModel constructor needs, plus the <see cref="DefaultHttpContext"/>
    /// backing them so the test can set <c>HttpContext.User</c> before calling into the model.
    /// </summary>
    public static (UserManager<PSUser> Users, SignInManager<PSUser> SignIn, DefaultHttpContext HttpContext, IServiceProvider Provider) BuildIdentityServices(PSDbContext context)
    {
        DefaultHttpContext httpContext = new();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(context);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpContext });

        // AddIdentity already wires up authentication and the Identity.Application cookie scheme that
        // SignInManager.SignInAsync writes to - an explicit AddAuthentication().AddCookie() here would
        // collide with it ("Scheme already exists").
        services.AddIdentity<PSUser, IdentityRole<long>>(o => o.SignIn.RequireConfirmedAccount = true)
                .AddEntityFrameworkStores<PSDbContext>()
                .AddDefaultTokenProviders();

        ServiceProvider provider = services.BuildServiceProvider();
        httpContext.RequestServices = provider;

        return (provider.GetRequiredService<UserManager<PSUser>>(),
                provider.GetRequiredService<SignInManager<PSUser>>(),
                httpContext,
                provider);
    }

    /// <summary>
    /// Sets <c>HttpContext.User</c> to a principal that resolves to <paramref name="user"/> via
    /// <see cref="UserManager{TUser}.GetUserAsync"/> - what a signed-in admin's request looks like.
    /// </summary>
    public static void SignInAsPrincipal(DefaultHttpContext httpContext, PSUser user)
    {
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
            IdentityConstants.ApplicationScheme));
    }

    /// <summary>A bare PageContext, enough for ModelState/HttpContext but no real routing.</summary>
    public static PageContext NewPageContext(DefaultHttpContext httpContext) => new()
    {
        HttpContext = httpContext,
    };

    /// <summary>
    /// A fake <see cref="IUrlHelper"/> whose only real behaviour is <see cref="IUrlHelper.RouteUrl"/>,
    /// since <c>Url.Page(...)</c> is implemented in terms of it. Renders a deterministic
    /// <c>"path?query"</c> string from the route values it's given, so a test can parse the accept-link
    /// or confirmation-link a PageModel builds without a real router.
    /// </summary>
    public static IUrlHelper NewUrlHelper(DefaultHttpContext httpContext) =>
        new FakeUrlHelper(new ActionContext(httpContext, new RouteData(), new PageActionDescriptor()));

    private sealed class FakeUrlHelper(ActionContext actionContext) : IUrlHelper
    {
        public ActionContext ActionContext { get; } = actionContext;

        public string RouteUrl(UrlRouteContext routeContext)
        {
            RouteValueDictionary values = new(routeContext.Values);

            string page = values.TryGetValue("page", out object? pageValue) ? pageValue!.ToString()! : string.Empty;
            values.Remove("page");

            string query = string.Join("&", values.Select(kv => $"{kv.Key}={kv.Value}"));

            return query.Length == 0 ? page : $"{page}?{query}";
        }

        public string? Action(UrlActionContext actionContext) => throw new NotSupportedException("Not used by the pages under test.");
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => url?.StartsWith('/') == true;
        public string? Link(string? routeName, object? values) => throw new NotSupportedException("Not used by the pages under test.");
    }
}
