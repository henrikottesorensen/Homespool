using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// One account per address, enforced by the database rather than by the validator that reads first.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests are about the write Identity has already approved.</b> <c>RequireUniqueEmail</c>
/// makes <c>UserValidator</c> refuse a second account on one address, and every test that goes through
/// an unmodified <see cref="UserManager{TUser}"/> stops there - which is why a missing index looked
/// like a constraint for as long as it did. A validator is a read before a write, so two requests can
/// each find no account for an address and each go on to create one: two invitation accepts, or two
/// first-run setup submissions. The index is what stands between them.
/// </para>
/// <para>
/// <b>So the validator is replaced with one that approves</b>, which is what the losing request saw:
/// a world with no account on that address, correctly observed and already out of date. Nothing else
/// about Identity is changed, so the write that arrives is the write the real path would send.
/// </para>
/// <para>
/// <b>The consequence being bought is wider than the duplicate row.</b> Sign-in, password reset,
/// confirmation resend, invite reactivation and the recipient's language all resolve an address to an
/// account and take the first row they find; two accounts on one address make every one of them pick
/// arbitrarily, so a reset link can arrive for the account it does not reset.
/// </para>
/// </remarks>
public sealed class NormalizedEmailUniquenessTests : IDisposable
{
    private const string Password = "Correct-Horse-Battery-Staple-1!";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-emailunique-{Guid.NewGuid():N}.db");

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

    /// <summary>
    /// A <see cref="UserManager{TUser}"/> whose user validation always passes - the state a request
    /// that lost the race is in, having read the table before the other write landed.
    /// </summary>
    private static UserManager<HSUser> UsersWithApprovingValidation(HomespoolDbContext context)
    {
        IUserValidator<HSUser> approving = Substitute.For<IUserValidator<HSUser>>();
        approving.ValidateAsync(Arg.Any<UserManager<HSUser>>(), Arg.Any<HSUser>())
                 .Returns(IdentityResult.Success);

        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(
            context,
            services =>
            {
                services.RemoveAll<IUserValidator<HSUser>>();
                services.AddSingleton(approving);
            });

        return users;
    }

    private static Task<IdentityResult> CreateAsync(UserManager<HSUser> users, string userName, string email)
    {
        HSUser user = new(userName)
        {
            Email = email,
            EmailConfirmed = true,
        };

        return users.CreateAsync(user, Password);
    }

    /// <summary>
    /// The finding itself: with nothing checking first, the second account on one address does not
    /// land.
    /// </summary>
    [Fact]
    public async Task ASecondAccountOnOneAddressIsRefusedByTheDatabase()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = UsersWithApprovingValidation(context);

        (await CreateAsync(users, "first", "shared@example.com")).Succeeded.Should().BeTrue();

        // Act
        Func<Task> second = () => CreateAsync(users, "second", "shared@example.com");

        // Assert
        await second.Should().ThrowAsync<DbUpdateException>("the unique index is the only thing left checking");

        // Read back rather than trusting the exception: what matters is that one account holds the
        // address, not that a call failed.
        int holders = await context.Users.AsNoTracking()
                                  .CountAsync(u => u.NormalizedEmail == "SHARED@EXAMPLE.COM",
                                              TestContext.Current.CancellationToken);

        holders.Should().Be(1);
    }

    /// <summary>
    /// Casing cannot slip a duplicate past it: the index is over the normalised column, which is the
    /// same value every address lookup in the application resolves against.
    /// </summary>
    [Fact]
    public async Task TheRefusalFollowsNormalisationRatherThanTheTypedCasing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = UsersWithApprovingValidation(context);

        (await CreateAsync(users, "first", "Shared@Example.com")).Succeeded.Should().BeTrue();

        // Act
        Func<Task> second = () => CreateAsync(users, "second", "SHARED@example.COM");

        // Assert
        await second.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>
    /// And it bounds nothing else: two accounts on two addresses are two accounts. Without this the
    /// tests above pass against an index that is unique over the wrong thing.
    /// </summary>
    [Fact]
    public async Task TwoAccountsOnTwoAddressesAreBothAccepted()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = UsersWithApprovingValidation(context);

        // Act
        IdentityResult first = await CreateAsync(users, "first", "one@example.com");
        IdentityResult second = await CreateAsync(users, "second", "two@example.com");

        // Assert
        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();

        int accounts = await context.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);

        accounts.Should().Be(2);
    }
}
