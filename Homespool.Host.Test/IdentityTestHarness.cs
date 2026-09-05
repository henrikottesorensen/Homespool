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
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Builds a real <see cref="UserManager{TUser}"/>/<see cref="SignInManager{TUser}"/> against a migrated
/// <see cref="HomespoolDbContext"/>, plus a minimal <see cref="IUrlHelper"/>, so <c>RegisterModel</c> and
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
    public static (UserManager<HSUser> users, SignInManager<HSUser> signIn, DefaultHttpContext httpContext, IServiceProvider
        provider) BuildIdentityServices(HomespoolDbContext context, Action<IServiceCollection>? configure = null)
    {
        DefaultHttpContext httpContext = new();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();

        // UsernameValidator words its refusals through the shared resources, and it runs here because
        // it decides what is accepted. Bare AddLocalization is enough: the strings resolve from the
        // Host assembly, and a test asserts on the error code rather than the wording.
        services.AddLocalization();
        services.AddSingleton(context);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = httpContext });

        // The Identity.Application cookie scheme is what SignInManager.SignInAsync writes to, and
        // nothing in AddHomespoolIdentity registers it: in the application it is the head of the
        // AddAuthentication chain in Program, and this is that head, without the printer, token and
        // OpenID Connect schemes no page model under test reaches.
        services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                    options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
                })
                .AddIdentityCookieSchemes()
                .AddPasskeyAuthentication();

        // The options come from the application's own configuration rather than being restated here,
        // so a test cannot create an account the real thing would refuse - see IdentityConfiguration.
        // The registration is the application's own for the same reason: a service the real thing
        // resolves differently would make this harness describe a different Identity.
        services.AddHomespoolIdentity(IdentityConfiguration.Configure)
                .AddHomespoolStores()
                .AddHomespoolTokenProviders();

        services.Configure<IdentityPasskeyOptions>(IdentityConfiguration.ConfigurePasskeys);

        // A test that needs a deployment choice - a relying-party id, say - makes it here, after the
        // application's registrations and before the container is built, the way Program would.
        configure?.Invoke(services);

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
    /// and has nothing to do with their address - <c>UsernameValidator</c> forbids the <c>@</c> that
    /// would let one be the other. This exists so a fixture that wants two distinct accounts can go
    /// on saying so with one string each. ASCII only, deliberately: a fixture name is not the place
    /// to exercise the validator.
    /// </remarks>
    public static string UsernameFor(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        int at = email.IndexOf('@', StringComparison.Ordinal);
        string local = at < 0 ? email : email[..at];

        return string.Concat(local.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '-'));
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
