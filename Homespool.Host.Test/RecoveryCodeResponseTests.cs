using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using OtpNet;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The two responses that carry recovery codes in the clear, and the header that keeps them from
/// being stored.
/// </summary>
/// <remarks>
/// <b>The header was arriving by luck, which is why these tests exist.</b> Every page under the
/// layout renders a form to sign out, generating an antiforgery token sets
/// <c>no-cache, no-store</c> on any response that carries one, and so both of these already sent it
/// without either handler saying so. That makes the protection a property of the navigation: turn
/// the sign-out form into a link and ten live credentials become storable, with nothing to fail.
/// These assert on the handler's own response, where only what the handler sets is present.
/// </remarks>
public sealed class RecoveryCodeResponseTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-codespage-{Guid.NewGuid():N}.db");
    private readonly List<IServiceScope> _scopes = [];

    public void Dispose()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Minting a fresh set: the response that shows them says both directives.
    /// </summary>
    [Fact]
    public async Task TheResponseShowingFreshCodesIsNotStorable()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, DefaultHttpContext httpContext) = await SignedInAsync(context);

        HSUser user = (await users.FindByNameAsync("owner"))!;
        (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.Should().BeTrue();

        GenerateRecoveryCodesModel model = new(users,
                                               NullLogger<GenerateRecoveryCodesModel>.Instance,
                                               TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };

        await model.OnPostAsync();

        model.RecoveryCodes.Should().NotBeNull("this is the response that carries them");
        httpContext.Response.Headers.CacheControl.ToString().Should().Be("no-cache, no-store");
    }

    /// <summary>
    /// The other minting site: a first enable issues the codes with the same response, and it says
    /// the same thing.
    /// </summary>
    [Fact]
    public async Task TheResponseIssuingCodesOnAFirstEnableIsNotStorable()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, DefaultHttpContext httpContext) = await SignedInAsync(context);

        HSUser user = (await users.FindByNameAsync("owner"))!;
        (await users.ResetAuthenticatorKeyAsync(user)).Succeeded.Should().BeTrue();
        string key = (await users.GetAuthenticatorKeyAsync(user))!;

        EnableAuthenticatorModel model = new(users,
                                             httpContext.RequestServices.GetRequiredService<LocalSignIn>(),
                                             new UnitOfWork(context),
                                             NullLogger<EnableAuthenticatorModel>.Instance,
                                             UrlEncoder.Default,
                                             TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new EnableAuthenticatorModel.InputModel
            {
                Code = LocalSchemeRig.CodeFor(Base32Encoding.ToBytes(key)),
            },
        };

        await model.OnPostAsync(CancellationToken.None);

        model.RecoveryCodes.Should().NotBeNull("a first enable mints them and shows them here");
        httpContext.Response.Headers.CacheControl.ToString().Should().Be("no-cache, no-store");
    }

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    /// <summary>
    /// An account and a request carrying its principal, each in a scope of its own - the handler
    /// provider caches per scope, and both pages resolve schemes through the request.
    /// </summary>
    private async Task<(UserManager<HSUser> users, DefaultHttpContext httpContext)> SignedInAsync(
        HomespoolDbContext context)
    {
        (UserManager<HSUser> users, _, _, IServiceProvider provider) =
            IdentityTestHarness.BuildIdentityServices(context);

        IServiceScope scope = provider.CreateScope();
        _scopes.Add(scope);

        DefaultHttpContext httpContext = new() { RequestServices = scope.ServiceProvider };
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("homespool.test");
        httpContext.Response.Body = new MemoryStream();

        HSUser user = new("owner") { Email = "owner@example.com", EmailConfirmed = true };
        (await users.CreateAsync(user, LocalSchemeRig.Password)).Succeeded.Should().BeTrue();
        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        return (users, httpContext);
    }
}
