using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The account's default printer, and the one question this service exists to answer: does a stored
/// choice still count.
/// </summary>
/// <remarks>
/// <para>
/// <b>The column is a plain id with no foreign key</b>, so it goes on naming a printer after the
/// printer is gone or the account has left the team that owns it. That is deliberate - it keeps
/// removal and team edits from owing this column a step - and it is only safe because reading
/// resolves rather than trusts. These cases are that resolution.
/// </para>
/// <para>
/// <b>The failure being designed against is a silent retarget</b>: a stale default that answers with
/// somebody else's machine, or a refusal that leaves the caller stuck with a choice they can no
/// longer see. So the interesting cases are all the negative ones.
/// </para>
/// </remarks>
public sealed class DefaultPrinterServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-default-printer-{Guid.NewGuid():N}.db");

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

    /// <summary>The ordinary path: chosen, stored, and read back.</summary>
    [Fact]
    public async Task APrinterTheCallerCanSeeIsStoredAndResolves()
    {
        await using HomespoolDbContext context = await SeedAsync();
        (DefaultPrinterService defaults, UserManager<HSUser> users) = Build(context);

        HSUser user = await SeedUserAsync(users, "chooser@example.com");
        await JoinAsync(context, user.Id);

        (await defaults.SetAsync(user, Caller.Unscoped(user.Id), 1, TestContext.Current.CancellationToken))
            .Should().BeTrue();

        user.DefaultPrinterId.Should().Be(1);

        (await defaults.ResolveAsync(user, Caller.Unscoped(user.Id), TestContext.Current.CancellationToken))
            .Should().Be(1);
    }

    /// <summary>
    /// A default on a team the account has since left resolves to nothing rather than to a printer
    /// they may no longer look at.
    /// </summary>
    [Fact]
    public async Task LeavingTheTeamLeavesTheAccountWithNoDefault()
    {
        await using HomespoolDbContext context = await SeedAsync();
        (DefaultPrinterService defaults, UserManager<HSUser> users) = Build(context);

        HSUser user = await SeedUserAsync(users, "left@example.com");
        TeamMember membership = await JoinAsync(context, user.Id);

        await defaults.SetAsync(user, Caller.Unscoped(user.Id), 1, TestContext.Current.CancellationToken);

        context.TeamMembers.Remove(membership);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A second service, because PrinterAccessService memoises "may this account touch this
        // printer" for the life of one request - deliberately, that being the only window in which
        // the answer cannot change. Asking again through the first one would be asking inside the
        // request that already answered.
        (await NewService(context, users).ResolveAsync(user, Caller.Unscoped(user.Id), TestContext.Current.CancellationToken))
            .Should().BeNull("the id is still stored, and it no longer names anything this account may see");
    }

    /// <summary>
    /// A default naming a removed printer resolves to nothing - which is what lets printer removal
    /// leave every account's column alone.
    /// </summary>
    [Fact]
    public async Task ARemovedPrinterLeavesTheAccountWithNoDefault()
    {
        await using HomespoolDbContext context = await SeedAsync();
        (DefaultPrinterService defaults, UserManager<HSUser> users) = Build(context);

        HSUser user = await SeedUserAsync(users, "orphaned@example.com");
        await JoinAsync(context, user.Id);

        await defaults.SetAsync(user, Caller.Unscoped(user.Id), 1, TestContext.Current.CancellationToken);

        context.Printers.Remove(await context.Printers.SingleAsync(p => p.Id == 1, TestContext.Current.CancellationToken));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A fresh request, for the memo reason given above.
        (await NewService(context, users).ResolveAsync(user, Caller.Unscoped(user.Id), TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    /// <summary>
    /// Naming a printer the caller cannot see is refused, and nothing is written - a form is the only
    /// way to ask for one, and a form can be edited.
    /// </summary>
    [Fact]
    public async Task APrinterTheCallerCannotSeeIsRefusedAndStoresNothing()
    {
        await using HomespoolDbContext context = await SeedAsync();
        (DefaultPrinterService defaults, UserManager<HSUser> users) = Build(context);

        HSUser stranger = await SeedUserAsync(users, "stranger@example.com");

        (await defaults.SetAsync(stranger, Caller.Unscoped(stranger.Id), 1, TestContext.Current.CancellationToken))
            .Should().BeFalse();

        stranger.DefaultPrinterId.Should().BeNull("a refusal that wrote the id anyway would be no refusal");
    }

    /// <summary>
    /// A credential whose scope withholds <see cref="Capability.ViewPrinter"/> reads no default, even
    /// though the membership behind it would allow one.
    /// </summary>
    /// <remarks>
    /// Both halves of the question are asked - may the team, and did this credential lend that power.
    /// A resolution that consulted only the membership would hand a narrowed token a printer its
    /// scope withheld.
    /// </remarks>
    [Fact]
    public async Task ANarrowedCredentialReadsNoDefault()
    {
        await using HomespoolDbContext context = await SeedAsync();
        (DefaultPrinterService defaults, UserManager<HSUser> users) = Build(context);

        HSUser user = await SeedUserAsync(users, "narrowed@example.com");
        await JoinAsync(context, user.Id);

        await defaults.SetAsync(user, Caller.Unscoped(user.Id), 1, TestContext.Current.CancellationToken);

        Caller narrowed = Caller.Scoped(
            user.Id,
            CapabilitySet.Parse(CapabilitySet.Format([Capability.ViewOwnFiles])));

        (await defaults.ResolveAsync(user, narrowed, TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    /// <summary>
    /// Clearing needs no permission on the printer, because the one account that most needs to clear
    /// a default is the one that can no longer see what it points at.
    /// </summary>
    [Fact]
    public async Task ADefaultCanBeClearedAfterItStopsBeingVisible()
    {
        await using HomespoolDbContext context = await SeedAsync();
        (DefaultPrinterService defaults, UserManager<HSUser> users) = Build(context);

        HSUser user = await SeedUserAsync(users, "clearing@example.com");
        TeamMember membership = await JoinAsync(context, user.Id);

        await defaults.SetAsync(user, Caller.Unscoped(user.Id), 1, TestContext.Current.CancellationToken);

        context.TeamMembers.Remove(membership);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await defaults.ClearAsync(user)).Should().BeTrue();

        user.DefaultPrinterId.Should().BeNull();
    }

    /// <summary>No choice made is null rather than a guess at one.</summary>
    [Fact]
    public async Task AnAccountThatHasChosenNothingResolvesToNothing()
    {
        await using HomespoolDbContext context = await SeedAsync();
        (DefaultPrinterService defaults, UserManager<HSUser> users) = Build(context);

        HSUser user = await SeedUserAsync(users, "undecided@example.com");
        await JoinAsync(context, user.Id);

        (await defaults.ResolveAsync(user, Caller.Unscoped(user.Id), TestContext.Current.CancellationToken))
            .Should().BeNull("a printer nobody picked is not a default");
    }

    private static (DefaultPrinterService defaults, UserManager<HSUser> users) Build(HomespoolDbContext context)
    {
        (UserManager<HSUser> users, _, _, _) = IdentityTestHarness.BuildIdentityServices(context);

        return (NewService(context, users), users);
    }

    /// <summary>
    /// One more service over the same context, standing in for a second request - the access memo is
    /// scoped to one, so anything asserting that a permission has <em>changed</em> needs a new one.
    /// </summary>
    private static DefaultPrinterService NewService(HomespoolDbContext context, UserManager<HSUser> users)
    {
        return new DefaultPrinterService(
            new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
            users);
    }

    private static async Task<HSUser> SeedUserAsync(UserManager<HSUser> users, string email)
    {
        HSUser user = new(IdentityTestHarness.UsernameFor(email))
        {
            Email = email,
            EmailConfirmed = true,
        };

        (await users.CreateAsync(user, "Correct-Horse-Battery-Staple-1!")).Succeeded.Should().BeTrue(); // betterleaks:allow

        return user;
    }

    private static async Task<TeamMember> JoinAsync(HomespoolDbContext context, long userId)
    {
        TeamMember membership = TestMemberships.Viewer(1, userId);

        context.TeamMembers.Add(membership);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return membership;
    }

    private async Task<HomespoolDbContext> SeedAsync()
    {
        HomespoolDbContext context = new(new DbContextOptionsBuilder<HomespoolDbContext>()
                                         .UseSqlite($"Data Source={_databasePath}")
                                         .Options);

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        context.Teams.Add(new Team { Id = 1, Name = "workshop" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Printers.Add(new Printer { Id = 1, Uuid = Guid.NewGuid(), TeamId = 1 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
