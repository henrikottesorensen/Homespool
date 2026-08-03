using System;
using System.Linq;
using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Builds a real <see cref="UserManager{TUser}"/>/<see cref="SignInManager{TUser}"/> against a migrated
/// <see cref="HSDbContext"/>, plus a minimal <see cref="IUrlHelper"/>, so <c>RegisterModel</c> and
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
    public static (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, IServiceProvider provider) BuildIdentityServices(HSDbContext context)
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
        //
        // The options come from the application's own configuration rather than being restated here,
        // so a test cannot create an account the real thing would refuse - see IdentityConfiguration.
        services.AddIdentity<HSUser, IdentityRole<long>>(IdentityConfiguration.Configure)
                .AddEntityFrameworkStores<HSDbContext>()
                .AddDefaultTokenProviders();

        ServiceProvider provider = services.BuildServiceProvider();
        httpContext.RequestServices = provider;

        return (provider.GetRequiredService<UserManager<HSUser>>(),
                provider.GetRequiredService<SignInManager<HSUser>>(),
                httpContext,
                provider);
    }

    /// <summary>
    /// A username for a fixture that identifies its account by an address: the address's local part,
    /// with anything Identity would refuse replaced by a hyphen.
    /// </summary>
    /// <remarks>
    /// <b>A test convenience, not a rule the application has.</b> A username is chosen by the person
    /// and has nothing to do with their address - <see cref="HSUser.AllowedUsernameCharacters"/>
    /// forbids the <c>@</c> that would let one be the other. This exists so a fixture that wants two
    /// distinct accounts can go on saying so with one string each.
    /// </remarks>
    public static string UsernameFor(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        int at = email.IndexOf('@', StringComparison.Ordinal);
        string local = at < 0 ? email : email[..at];

        return string.Concat(local.Select(c => HSUser.AllowedUsernameCharacters.Contains(c, StringComparison.Ordinal) ? c : '-'));
    }

    /// <summary>
    /// Sets <c>HttpContext.User</c> to a principal that resolves to <paramref name="user"/> via
    /// <see cref="UserManager{TUser}.GetUserAsync"/> - what a signed-in admin's request looks like.
    /// </summary>
    public static void SignInAsPrincipal(DefaultHttpContext httpContext, HSUser user)
    {
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
            IdentityConstants.ApplicationScheme));
    }

    /// <summary>A bare PageContext, enough for ModelState/HttpContext but no real routing.</summary>
    public static PageContext NewPageContext(DefaultHttpContext httpContext)
    {
        return new()
        {
            HttpContext = httpContext,
        };
    }

    /// <summary>
    /// A fake <see cref="IUrlHelper"/> whose only real behaviour is <see cref="IUrlHelper.RouteUrl"/>,
    /// since <c>Url.Page(...)</c> is implemented in terms of it. Renders a deterministic
    /// <c>"path?query"</c> string from the route values it's given, so a test can parse the accept-link
    /// or confirmation-link a PageModel builds without a real router.
    /// </summary>
    public static IUrlHelper NewUrlHelper(DefaultHttpContext httpContext)
    {
        return new FakeUrlHelper(new ActionContext(httpContext, new RouteData(), new PageActionDescriptor()));
    }

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

        public string? Action(UrlActionContext actionContext)
        {
            throw new NotSupportedException("Not used by the pages under test.");
        }

        public string? Content(string? contentPath)
        {
            return contentPath;
        }

        public bool IsLocalUrl(string? url)
        {
            return url?.StartsWith('/') == true;
        }

        public string? Link(string? routeName, object? values)
        {
            throw new NotSupportedException("Not used by the pages under test.");
        }
    }
}
