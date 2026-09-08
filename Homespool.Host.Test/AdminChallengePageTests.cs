using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Admin;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The one door into the administration screens: the password that opens it, and what happens to
/// somebody who gets it wrong.
/// </summary>
/// <remarks>
/// <b>A wrong password must leave the browser exactly as unelevated as it arrived</b>, which is the
/// test to write first - a page that granted regardless would satisfy every assertion about the
/// happy path. Checked by mutation: granting before the proof is read leaves that one test red and
/// the rest green.
/// </remarks>
public sealed class AdminChallengePageTests : IDisposable
{
    private const string Password = "Correct horse battery staple 1"; // betterleaks:allow

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-adminchallenge-{Guid.NewGuid():N}.db");

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

    [Fact]
    public async Task TheRightPasswordElevatesAndGoesWhereTheyWereHeading()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        AdminElevation elevation = NewElevation();
        (ChallengeModel model, DefaultHttpContext request) = NewModel(provider, users, elevation, admin, Password);
        model.ReturnUrl = "/Admin/Users/Detail/3";

        // Act
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Admin/Users/Detail/3");
        Elevated(elevation, request, admin).Should().BeTrue();
    }

    [Fact]
    public async Task AWrongPasswordElevatesNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        AdminElevation elevation = NewElevation();
        (ChallengeModel model, DefaultHttpContext request) =
            NewModel(provider, users, elevation, admin, "not it"); // betterleaks:allow

        // Act
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<PageResult>();
        model.StatusMessage.Should().Be("That is not your password.");
        Elevated(elevation, request, admin).Should().BeFalse();
    }

    /// <summary>
    /// A return address outside the administration screens is not followed. It would make the page
    /// an open redirect, and an elevation earned for these screens has no business delivering the
    /// browser anywhere else.
    /// </summary>
    [Theory]
    [InlineData("https://elsewhere.example.com/")]
    [InlineData("/Account/Manage/ApiTokens")]
    [InlineData(null)]
    public async Task AReturnAddressOutsideTheAdministrationScreensGoesToTheRoster(string? returnUrl)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        (ChallengeModel model, _) = NewModel(provider, users, NewElevation(), admin, Password);
        model.ReturnUrl = returnUrl;

        // Act
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Contain("/Admin/Users/Index");
    }

    /// <summary>An administrator who is already elevated is not asked again.</summary>
    [Fact]
    public async Task AnElevatedAdministratorIsSentStraightOn()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);
        HSUser admin = await AddUserAsync(users, "admin@example.com");
        AdminElevation elevation = NewElevation();

        (ChallengeModel granting, DefaultHttpContext granted) = NewModel(provider, users, elevation, admin, Password);
        await granting.OnPostAsync();

        (ChallengeModel model, DefaultHttpContext request) = NewModel(provider, users, elevation, admin, password: null);
        Carry(granted, request);
        model.ReturnUrl = "/Admin/Settings";

        // Act
        IActionResult result = await model.OnGetAsync();

        // Assert
        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Admin/Settings");
    }

    private static AdminElevation NewElevation()
    {
        return new AdminElevation(new EphemeralDataProtectionProvider(), TimeProvider.System);
    }

    /// <summary>Whether the response elevated <paramref name="admin"/>, read as the next request would.</summary>
    private static bool Elevated(AdminElevation elevation, DefaultHttpContext granting, HSUser admin)
    {
        DefaultHttpContext next = new();
        Carry(granting, next);

        return elevation.IsElevated(next, admin.Id);
    }

    /// <summary>Carries whatever <paramref name="from"/> set as cookies into <paramref name="to"/>.</summary>
    private static void Carry(DefaultHttpContext from, DefaultHttpContext to)
    {
        string setCookie = from.Response.Headers.SetCookie.ToString();

        if (!string.IsNullOrEmpty(setCookie))
        {
            to.Request.Headers.Cookie = setCookie.Split(';')[0];
        }
    }

    private (ChallengeModel model, DefaultHttpContext request) NewModel(IServiceProvider provider,
                                                                        UserManager<HSUser> users,
                                                                        AdminElevation elevation,
                                                                        HSUser admin,
                                                                        string? password)
    {
        // A scope per request, as a real request has: the authentication handlers are scoped and
        // memoise their first answer.
        IServiceScope scope = provider.CreateScope();
        _scopes.Add(scope);

        DefaultHttpContext request = new() { RequestServices = scope.ServiceProvider };
        request.Request.Scheme = "https";
        request.Request.Host = new HostString("homespool.test");
        request.Response.Body = new MemoryStream();

        IdentityTestHarness.SignInAsPrincipal(request, admin);

        ChallengeModel model = new(users,
                                   elevation,
                                   scope.ServiceProvider.GetRequiredService<StepUpGate>(),
                                   new StepUpText(TestLocaliser.Shared()),
                                   scope.ServiceProvider.GetRequiredService<ExternalSignIn>())
        {
            PageContext = IdentityTestHarness.NewPageContext(request),
            Url = IdentityTestHarness.NewUrlHelper(request),
            Input = new ChallengeModel.InputModel { Password = password },
        };

        return (model, request);
    }

    private static async Task<HSUser> AddUserAsync(UserManager<HSUser> users, string email)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = true,
        };

        IdentityResult created = await users.CreateAsync(user, Password);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

        return user;
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
