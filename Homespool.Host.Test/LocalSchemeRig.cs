using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OtpNet;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// A real Identity stack over a migrated database for driving the local credential schemes: builds
/// a request carrying a posted form and whichever Identity cookies a test wants, and resolves the
/// scheme's handler over it.
/// </summary>
/// <remarks>
/// <b>A scope per request, deliberately.</b> The handler provider caches handler instances per DI
/// scope, and a handler holds the context it was initialised with, so two requests sharing one scope
/// silently read the first request's cookies. Every request here gets its own scope.
/// </remarks>
internal sealed class LocalSchemeRig : IAsyncDisposable
{
    public const string Password = "Correct horse battery staple 1"; // betterleaks:allow

    private readonly HomespoolDbContext _context;
    private readonly ServiceProvider _provider;
    private readonly List<IServiceScope> _scopes = [];

    private LocalSchemeRig(HomespoolDbContext context, ServiceProvider provider)
    {
        _context = context;
        _provider = provider;
    }

    public UserManager<HSUser> Users => _provider.GetRequiredService<UserManager<HSUser>>();

    public static async Task<LocalSchemeRig> CreateAsync(string databasePath)
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        (_, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);

        return new LocalSchemeRig(context, (ServiceProvider)provider);
    }

    /// <summary>A confirmed account with a password.</summary>
    public async Task<HSUser> AddUserAsync(string email, bool confirmed = true)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = confirmed,
        };

        IdentityResult created = await Users.CreateAsync(user, Password);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

        return user;
    }

    /// <summary>Turns an authenticator on for the account and returns its secret, for computing codes.</summary>
    public async Task<byte[]> EnableAuthenticatorAsync(HSUser user)
    {
        (await Users.ResetAuthenticatorKeyAsync(user)).Succeeded.Should().BeTrue();
        (await Users.SetTwoFactorEnabledAsync(user, true)).Succeeded.Should().BeTrue();

        string key = (await Users.GetAuthenticatorKeyAsync(user))!;

        return Base32Encoding.ToBytes(key);
    }

    /// <summary>A code an authenticator app would show right now for <paramref name="secret"/>.</summary>
    public static string CodeFor(byte[] secret)
    {
        return new Totp(secret).ComputeTotp();
    }

    /// <summary>A post to the login page carrying <paramref name="cookies"/>, with nothing presented yet.</summary>
    public DefaultHttpContext NewRequest(params string[] cookies)
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);

        DefaultHttpContext request = new() { RequestServices = scope.ServiceProvider };
        request.Request.Scheme = "https";
        request.Request.Host = new HostString("homespool.test");
        request.Request.Path = "/Account/Login";
        request.Request.Method = HttpMethods.Post;

        if (cookies.Length > 0)
        {
            request.Request.Headers.Cookie = string.Join("; ", cookies);
        }

        return request;
    }

    /// <summary>Presents <paramref name="credentials"/> and authenticates <paramref name="scheme"/>, the way a page would.</summary>
    public static Task<AuthenticateResult> AuthenticateAsync(DefaultHttpContext request, string scheme, params object[] credentials)
    {
        return request.AuthenticateWithAsync(scheme, credentials);
    }

    /// <summary>The sign-in writer, from the request's own scope.</summary>
    public static LocalSignIn SignInOf(DefaultHttpContext request)
    {
        return request.RequestServices.GetRequiredService<LocalSignIn>();
    }

    /// <summary>The rules, from the request's own scope.</summary>
    public static LocalSignInRules RulesOf(DefaultHttpContext request)
    {
        return request.RequestServices.GetRequiredService<LocalSignInRules>();
    }

    /// <summary>The name of the cookie <paramref name="scheme"/> writes.</summary>
    public string CookieNameOf(string scheme)
    {
        return _provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme).Cookie.Name!;
    }

    /// <summary>
    /// The cookie <paramref name="scheme"/> set on <paramref name="request"/>, as a browser would send
    /// it back: name and value.
    /// </summary>
    public string CookieOf(DefaultHttpContext request, string scheme)
    {
        string name = CookieNameOf(scheme);
        string? header = request.Response.Headers.SetCookie
                                .FirstOrDefault(value => value is not null && value.StartsWith(name + "=", StringComparison.Ordinal) && !value.StartsWith(name + "=;", StringComparison.Ordinal));
        header.Should().NotBeNull($"the request should have set the {scheme} cookie");

        return header![..header.IndexOf(';', StringComparison.Ordinal)];
    }

    /// <summary>Whether <paramref name="request"/> told the browser to drop the cookie <paramref name="scheme"/> writes.</summary>
    public bool Cleared(DefaultHttpContext request, string scheme)
    {
        string name = CookieNameOf(scheme);

        return request.Response.Headers.SetCookie.Any(value => value is not null && value.StartsWith(name + "=;", StringComparison.Ordinal));
    }

    /// <summary>The pending-two-factor cookie for <paramref name="user"/>, as the password step writes it.</summary>
    public async Task<string> PendingTwoFactorCookieAsync(HSUser user, string? loginProvider = null)
    {
        DefaultHttpContext request = NewRequest();
        await SignInOf(request).BeginSecondFactorAsync(request, user, loginProvider);

        return CookieOf(request, IdentityConstants.TwoFactorUserIdScheme);
    }

    /// <summary>The application cookie for <paramref name="user"/>, as a completed sign-in writes it.</summary>
    public async Task<string> SessionCookieAsync(HSUser user)
    {
        DefaultHttpContext request = NewRequest();
        await SignInOf(request).SignInAsync(request, await PrincipalOf(user), isPersistent: false);

        return CookieOf(request, IdentityConstants.ApplicationScheme);
    }

    /// <summary>The remembered-machine cookie for <paramref name="user"/>, as a second factor with "remember" writes it.</summary>
    public async Task<string> RememberedMachineCookieAsync(HSUser user)
    {
        DefaultHttpContext request = NewRequest();
        await SignInOf(request).RememberClientAsync(request, user);

        return CookieOf(request, IdentityConstants.TwoFactorRememberMeScheme);
    }

    /// <summary>The principal the claims factory builds for <paramref name="user"/>, as a scheme would hand it over.</summary>
    public Task<ClaimsPrincipal> PrincipalOf(HSUser user)
    {
        return _provider.GetRequiredService<IUserClaimsPrincipalFactory<HSUser>>().CreateAsync(user);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        await _provider.DisposeAsync();
        await _context.DisposeAsync();
    }
}
