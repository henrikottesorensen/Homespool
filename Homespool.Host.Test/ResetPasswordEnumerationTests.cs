using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Pages.Account;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// What <c>Account/ResetPassword</c> answers an address no account holds, against what it answers an
/// address one does: the same thing, because the form is anonymous and the difference was readable
/// from outside.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property is that both refusals are one response.</b> A caller who holds no token cannot
/// pass either, so the only thing the two branches can differ in is what they say about the address -
/// and an unknown address sent to the confirmation page while a known one re-rendered the form said
/// it in the status code alone, at one request per guess, with nothing signed in.
/// </para>
/// <para>
/// <b>Both halves are asserted, and the second is the one that rots.</b> Making the pair identical is
/// easy to do by rendering nothing useful to either; the test therefore also requires that the shared
/// answer is not empty, so the person whose link expired is still told why.
/// </para>
/// </remarks>
public sealed class ResetPasswordEnumerationTests : IDisposable
{
    private const string Password = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow

    /// <summary>Not a token, and not close to one: no caller without a mailed link has better.</summary>
    private const string NotAToken = "not-a-token";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-pwenum-{Guid.NewGuid():N}.db");

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
    public async Task AnAddressNobodyHoldsIsRefusedExactlyAsAKnownOneWithABadCodeIs()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new(IdentityTestHarness.UsernameFor("holder@example.com"))
        {
            Email = "holder@example.com",
            EmailConfirmed = true,
        };

        (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue("the account is setup, not the subject");

        // Act - the same garbage code against an address that exists and one that does not
        (IActionResult known, ModelStateDictionary knownState) =
            await PostAsync(context, users, httpContext, "holder@example.com");
        (IActionResult unknown, ModelStateDictionary unknownState) =
            await PostAsync(context, users, httpContext, "nobody@example.com");

        // Assert
        known.Should().BeOfType<PageResult>();
        unknown.Should().BeOfType<PageResult>("a redirect where the known address re-renders is what "
                                              + "told an anonymous caller which addresses exist");
        Errors(unknownState).Should().Equal(Errors(knownState), "one answer, in the same words");
        Errors(knownState).Should().NotBeEmpty("the person whose link expired is still told why it failed");
    }

    /// <summary>Posts the form for <paramref name="email"/> with a code that cannot verify.</summary>
    private static async Task<(IActionResult result, ModelStateDictionary state)> PostAsync(
        HomespoolDbContext context,
        UserManager<HSUser> users,
        DefaultHttpContext httpContext,
        string email)
    {
        ResetPasswordModel model = new(users,
                                       new ApiTokenService(context),
                                       new UnitOfWork(context),
                                       new AttemptLimiter(context,
                                                          TestOptions.Snapshot(new AttemptLimitOptions()),
                                                          NullLogger<AttemptLimiter>.Instance),
                                       TestLocaliser.Shared(),
                                       NullLogger<ResetPasswordModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
            Input = new ResetPasswordModel.InputModel
            {
                Email = email,
                Password = Password,
                ConfirmPassword = Password,
                Code = NotAToken,
            },
        };

        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        return (result, model.ModelState);
    }

    private static IEnumerable<string> Errors(ModelStateDictionary state)
    {
        return state.Values.SelectMany(entry => entry.Errors).Select(error => error.ErrorMessage).ToList();
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
