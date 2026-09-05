using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// The cookie a sign-in wrote on <paramref name="request"/>, as a browser would send it back: the
    /// first Set-Cookie's name and value.
    /// </summary>
    public static string CookieOf(DefaultHttpContext request)
    {
        string header = request.Response.Headers.SetCookie.ToString();
        header.Should().NotBeNullOrEmpty("the request should have set a cookie");

        return header[..header.IndexOf(';', StringComparison.Ordinal)];
    }

    /// <summary>The pending-two-factor cookie for <paramref name="user"/>, as the password step writes it.</summary>
    public async Task<string> PendingTwoFactorCookieAsync(HSUser user)
    {
        DefaultHttpContext request = NewRequest();
        await request.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, LocalSignInRules.PendingTwoFactor(user));

        return CookieOf(request);
    }

    /// <summary>The application cookie for <paramref name="user"/>, as a completed sign-in writes it.</summary>
    public async Task<string> SessionCookieAsync(HSUser user)
    {
        DefaultHttpContext request = NewRequest();
        ClaimsPrincipal principal = await _provider.GetRequiredService<IUserClaimsPrincipalFactory<HSUser>>().CreateAsync(user);
        await request.SignInAsync(IdentityConstants.ApplicationScheme, principal, new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow });

        return CookieOf(request);
    }

    /// <summary>The remembered-machine cookie for <paramref name="user"/>, as a second factor with "remember" writes it.</summary>
    public async Task<string> RememberedMachineCookieAsync(HSUser user)
    {
        DefaultHttpContext request = NewRequest();
        ClaimsIdentity identity = new(IdentityConstants.TwoFactorRememberMeScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Id.ToString(CultureInfo.InvariantCulture)));
        await request.SignInAsync(IdentityConstants.TwoFactorRememberMeScheme, new ClaimsPrincipal(identity));

        return CookieOf(request);
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
