using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="IdentityConfiguration.EmailOf"/> answers a stored account's address, and the rule that
/// lets it promise one.
/// </summary>
/// <remarks>
/// The helper is only as good as <c>RequireUniqueEmail</c>, so the second half creates and updates
/// accounts through the same Identity configuration the application uses and shows a blank address
/// refused both ways. If that setting were dropped, these fail before any page mails nobody.
/// </remarks>
public sealed class EmailOfTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-email-of-{Guid.NewGuid():N}.db");

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

    /// <summary>An account with an address gets that address back, unchanged.</summary>
    [Fact]
    public void AnAccountWithAnAddressAnswersIt()
    {
        HSUser user = new("anna") { Email = "Anna@example.net" };

        IdentityConfiguration.EmailOf(user).Should().Be("Anna@example.net");
    }

    /// <summary>
    /// An account with no usable address throws rather than handing a mail sender nothing, and says
    /// which account it was.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAccountWithoutAnAddressThrows(string? email)
    {
        HSUser user = new("anna") { Id = 42, Email = email };

        Action read = () => IdentityConfiguration.EmailOf(user);

        read.Should().Throw<InvalidOperationException>().WithMessage("Account 42 *");
    }

    /// <summary>Identity refuses to create an account without an address.</summary>
    [Fact]
    public async Task AnAccountCannotBeCreatedWithoutAnAddress()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);

        IdentityResult created = await users.CreateAsync(new HSUser("anna"), "Correct-Horse-Battery-Staple-1!");

        created.Succeeded.Should().BeFalse();
        created.Errors.Should().Contain(error => error.Code == nameof(IdentityErrorDescriber.InvalidEmail));
    }

    /// <summary>Identity refuses to save an account whose address has been blanked.</summary>
    [Fact]
    public async Task AnAccountCannotBeUpdatedToLoseItsAddress()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new("anna") { Email = "anna@example.net" };
        (await users.CreateAsync(user, "Correct-Horse-Battery-Staple-1!")).Succeeded.Should().BeTrue();

        IdentityResult blanked = await users.SetEmailAsync(user, null);

        blanked.Succeeded.Should().BeFalse();
        blanked.Errors.Should().Contain(error => error.Code == nameof(IdentityErrorDescriber.InvalidEmail));
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
