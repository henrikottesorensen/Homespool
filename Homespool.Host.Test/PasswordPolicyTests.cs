using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The password length floor, exercised through the same Identity services the application
/// configures - see <c>IdentityConfiguration</c>.
/// </summary>
/// <remarks>
/// Identity's own default is 6, which predates current guidance; 8 is NIST SP 800-63B's floor for a
/// user-chosen password. Driven through <see cref="UserManager{TUser}"/> rather than asserted on the
/// options object, so what is pinned is what an account creation actually gets refused with.
/// </remarks>
public sealed class PasswordPolicyTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-password-policy-{Guid.NewGuid():N}.db");

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
    public async Task ASevenCharacterPasswordIsRefusedAndAnEighthCharacterFixesIt()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser tooShort = new("floors") { Email = "floors@example.com" };
        IdentityResult refused = await users.CreateAsync(tooShort, "Ab1!def"); // betterleaks:allow

        refused.Succeeded.Should().BeFalse("seven characters is below the floor");
        refused.Errors.Select(e => e.Code).Should().Contain("PasswordTooShort");

        HSUser longEnough = new("floors") { Email = "floors@example.com" };
        (await users.CreateAsync(longEnough, "Ab1!defg")).Succeeded // betterleaks:allow
            .Should().BeTrue("eight characters is the floor, not more");
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
