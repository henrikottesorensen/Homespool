using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The current password on the change page is a step-up: a wrong one is counted against the account's
/// step-up backoff, a backed-off account is refused before anything is compared, and the account
/// lockout is never touched.
/// </summary>
/// <remarks>
/// <b>The page asks for no recent proof because the current password is the proof</b>, so an
/// uncounted comparison would let whoever holds the session guess at it at request rate. The backed-off
/// case posts the <i>right</i> password, because a refusal that only ever met wrong ones would pass
/// just as well if the page compared first and refused second.
/// </remarks>
public sealed class ChangePasswordAttemptTests : IDisposable
{
    private const string NewPassword = "Different horse battery staple 2"; // betterleaks:allow

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-pwattempt-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task AWrongCurrentPasswordIsCountedAgainstTheStepUpAndNotTheLockout()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@homespool.example.net");
        ChangePasswordModel model = await PageAsync(rig, user, "not the password");

        // Act
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<PageResult>();
        model.ModelState[string.Empty]!.Errors.Should().ContainSingle(e => e.ErrorMessage == "That is not your password.");
        (await StepUpFailuresAsync(model, user)).Should().Be(1);
        (await rig.Users.GetAccessFailedCountAsync(user))
            .Should().Be(0, "a session holder guessing here must not be able to lock the owner out of signing in");
    }

    [Fact]
    public async Task ABackedOffAccountIsRefusedEvenTheRightPassword()
    {
        // Arrange - the allowance and one past it, since the backoff starts on the failure that
        // exceeds it.
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@homespool.example.net");

        for (int attempt = 0; attempt <= new AttemptLimitOptions().MaxFailedAttempts; attempt += 1)
        {
            await (await PageAsync(rig, user, "not the password")).OnPostAsync();
        }

        ChangePasswordModel model = await PageAsync(rig, user, LocalSchemeRig.Password);

        // Act
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<PageResult>();
        model.ModelState[string.Empty]!.Errors
             .Should().ContainSingle(e => e.ErrorMessage.StartsWith("Too many wrong passwords.", StringComparison.Ordinal));
        (await rig.Users.CheckPasswordAsync(user, LocalSchemeRig.Password))
            .Should().BeTrue("a refused change leaves the password as it was");
        (await rig.Users.IsLockedOutAsync(user))
            .Should().BeFalse("the backoff is the step-up's own, so the owner can still sign in");
    }

    [Fact]
    public async Task TheRightCurrentPasswordChangesItAndClearsTheCount()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@homespool.example.net");
        await (await PageAsync(rig, user, "not the password")).OnPostAsync();

        ChangePasswordModel model = await PageAsync(rig, user, LocalSchemeRig.Password);

        // Act
        IActionResult result = await model.OnPostAsync();

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        (await rig.Users.CheckPasswordAsync(user, NewPassword)).Should().BeTrue();
        (await StepUpFailuresAsync(model, user)).Should().Be(0, "a right step-up clears the step-up backoff");
    }

    /// <summary>
    /// The page over a request of its own, signed in as <paramref name="user"/> and posting
    /// <paramref name="currentPassword"/>: a scope per request, since a handler instance remembers the
    /// request it first authenticated.
    /// </summary>
    private static async Task<ChangePasswordModel> PageAsync(LocalSchemeRig rig, HSUser user, string currentPassword)
    {
        DefaultHttpContext request = rig.NewRequest(await rig.SessionCookieAsync(user));
        request.User = await rig.PrincipalOf(user);
        IServiceProvider services = request.RequestServices;

        return new ChangePasswordModel(services.GetRequiredService<UserManager<HSUser>>(),
                                       services.GetRequiredService<LocalSignIn>(),
                                       services.GetRequiredService<StepUpGate>(),
                                       new StepUpText(TestLocaliser.Shared()),
                                       TestLocaliser.Shared(),
                                       NullLogger<ChangePasswordModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(request),
            Input = new ChangePasswordModel.InputModel
            {
                OldPassword = currentPassword,
                NewPassword = NewPassword,
                ConfirmPassword = NewPassword,
            },
        };
    }

    /// <summary>The step-up failures counted against <paramref name="user"/>, read from the database; zero when there is no row.</summary>
    private static async Task<int> StepUpFailuresAsync(PageModel model, HSUser user)
    {
        HomespoolDbContext context = model.HttpContext.RequestServices.GetRequiredService<HomespoolDbContext>();

        return await context.UserActionAttempts
                            .AsNoTracking()
                            .Where(attempt => attempt.UserId == user.Id && attempt.Action == LimitedAction.StepUp)
                            .Select(attempt => attempt.FailedCount)
                            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }
}
