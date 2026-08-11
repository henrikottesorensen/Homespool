using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrinterAccessService"/> - the one answer to "may this account do this to this printer",
/// which was six answers until 2026-08-03.
/// </summary>
/// <remarks>
/// <para>
/// The cases worth having are the <b>refusal shapes</b>, not the happy path. Three entry points refuse
/// three different ways on purpose, and flattening any of them would be a security change wearing a
/// tidy-up's clothes: <see cref="PrinterAccessService.FindAsync"/> answering an exception instead of
/// null would let a caller enumerate other people's printers by watching which UUID failed differently.
/// </para>
/// <para>
/// The operation-to-permission mapping is covered exhaustively, because it is the one table nothing
/// else in the suite would notice going wrong - a mis-mapped operation grants rather than refuses.
/// </para>
/// </remarks>
public sealed class PrinterAccessServiceTests : IDisposable
{
    private const long Reader = 1;
    private const long User = 2;
    private const long Manager = 3;
    private const long Stranger = 4;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-access-{Guid.NewGuid():N}.db");

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
    /// Every operation against every level of membership. <b>The mapping table, asserted</b> - a wrong
    /// entry here grants access rather than refusing it, which nothing else would catch.
    /// </summary>
    [Theory]
    [InlineData(PrinterOperation.ViewPrinter, Reader, true)]
    [InlineData(PrinterOperation.ViewQueue, Reader, true)]
    [InlineData(PrinterOperation.ViewHistory, Reader, true)]
    [InlineData(PrinterOperation.ChangeQueue, Reader, false)]
    [InlineData(PrinterOperation.ControlPrinter, Reader, false)]
    [InlineData(PrinterOperation.ManagePrinter, Reader, false)]
    [InlineData(PrinterOperation.ViewQueue, User, true)]
    [InlineData(PrinterOperation.ChangeQueue, User, true)]
    [InlineData(PrinterOperation.ControlPrinter, User, true)]
    [InlineData(PrinterOperation.ManagePrinter, User, false)]
    [InlineData(PrinterOperation.ChangeQueue, Manager, true)]
    [InlineData(PrinterOperation.ManagePrinter, Manager, true)]
    [InlineData(PrinterOperation.ViewPrinter, Stranger, false)]
    [InlineData(PrinterOperation.ChangeQueue, Stranger, false)]
    public async Task EachOperationNeedsThePermissionItIsMappedTo(PrinterOperation operation, long userId,
        bool expected)
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        PrinterAccessService access = new(context);

        // Act
        bool allowed = await access.AllowsAsync(1, userId, operation, TestContext.Current.CancellationToken);

        // Assert
        allowed.Should().Be(expected);
    }

    /// <summary>
    /// <see cref="PrinterAccessService.RequireAsync"/> tells the two failures apart, because its
    /// callers already knew the printer existed.
    /// </summary>
    [Fact]
    public async Task RequireDistinguishesAMissingPrinterFromARefusedOne()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        PrinterAccessService access = new(context);

        // Act & Assert
        await FluentActions
              .Awaiting(() => access.RequireAsync(999, Reader, PrinterOperation.ViewPrinter,
                   TestContext.Current.CancellationToken))
              .Should().ThrowAsync<PrinterNotFoundException>();

        await FluentActions
              .Awaiting(() => access.RequireAsync(1, Reader, PrinterOperation.ChangeQueue,
                   TestContext.Current.CancellationToken))
              .Should().ThrowAsync<TeamAccessDeniedException>();
    }

    /// <summary>
    /// <see cref="PrinterAccessService.FindAsync"/> refuses to tell them apart, which is the whole
    /// point of it existing separately.
    /// </summary>
    /// <remarks>
    /// <b>Not a duplicate of the test above.</b> If these two ever answer alike, a caller can learn
    /// that a UUID belongs to somebody by the shape of the refusal - the exact leak the null return
    /// exists to close.
    /// </remarks>
    [Fact]
    public async Task FindAnswersNullForBothAnUnknownPrinterAndOneYouMayNotSee()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        PrinterAccessService access = new(context);
        Guid known = await context.Printers.Select(printer => printer.Uuid)
                                  .SingleAsync(TestContext.Current.CancellationToken);

        // Act
        Printer? unknown = await access.FindAsync(Guid.NewGuid(), Reader, PrinterOperation.ViewPrinter,
            TestContext.Current.CancellationToken);

        Printer? forbidden = await access.FindAsync(known, Stranger, PrinterOperation.ViewPrinter,
            TestContext.Current.CancellationToken);

        // Assert
        unknown.Should().BeNull();
        forbidden.Should().BeNull("a caller who could tell these apart could enumerate other teams' printers");
    }

    /// <summary>
    /// The default operation grants nothing, on a member with every permission there is.
    /// </summary>
    /// <remarks>
    /// <b>The one case that fails open if it regresses.</b> Before
    /// <see cref="PrinterOperation.Undefined"/> existed, zero was <c>ViewPrinter</c> - so an
    /// uninitialised field or a deserialised zero asked for the most permissive read and got it.
    /// Throwing is right rather than returning false: nothing legitimately asks this, so it is a
    /// programming error rather than a refusal.
    /// </remarks>
    [Fact]
    public async Task TheDefaultOperationIsNotAPermissionAnybodyHas()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        PrinterAccessService access = new(context);

        // Act & Assert
        await FluentActions
              .Awaiting(() => access.AllowsAsync(1, Manager, default, TestContext.Current.CancellationToken))
              .Should().ThrowAsync<ArgumentOutOfRangeException>("a default operation must not resolve to a real one");
    }

    /// <summary>A printer that does not exist is not something anybody may act on.</summary>
    [Fact]
    public async Task AllowsIsFalseForAPrinterThatDoesNotExist()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        PrinterAccessService access = new(context);

        // Act
        bool allowed = await access.AllowsAsync(999, Manager, PrinterOperation.ViewPrinter,
            TestContext.Current.CancellationToken);

        // Assert
        allowed.Should().BeFalse();
    }

    /// <summary>
    /// The memo answers from the scope rather than the database, and does not confuse two callers.
    /// </summary>
    /// <remarks>
    /// Proved by changing the row underneath: a second ask returns the <i>first</i> answer, which is
    /// what "a request cannot change its own permissions part-way through" means in practice. The
    /// second half is the one that would be a security bug rather than a stale read - a memo keyed on
    /// the printer alone would hand one user another's permissions.
    /// </remarks>
    [Fact]
    public async Task TheMemoIsPerRequestAndPerUser()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        PrinterAccessService access = new(context);

        (await access.AllowsAsync(1, Reader, PrinterOperation.ChangeQueue, TestContext.Current.CancellationToken))
            .Should().BeFalse();

        // Act - grant it behind the service's back
        TeamMember member = await context.TeamMembers.SingleAsync(m => m.UserId == Reader,
            TestContext.Current.CancellationToken);
        member.CanUse = true;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Assert
        (await access.AllowsAsync(1, Reader, PrinterOperation.ChangeQueue, TestContext.Current.CancellationToken))
            .Should().BeFalse("the answer was already given in this scope");

        (await access.AllowsAsync(1, Manager, PrinterOperation.ManagePrinter, TestContext.Current.CancellationToken))
            .Should().BeTrue("a memo keyed on the printer alone would answer for the wrong person");
    }

    private async Task<HomespoolDbContext> SeedAsync()
    {
        HomespoolDbContext context = new(new DbContextOptionsBuilder<HomespoolDbContext>()
                                  .UseSqlite($"Data Source={_databasePath}")
                                  .Options);

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = Reader, CanRead = true });
        context.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = User, CanRead = true, CanUse = true });
        context.TeamMembers.Add(new TeamMember
        {
            TeamId = team.Id,
            UserId = Manager,
            CanRead = true,
            CanUse = true,
            CanManage = true,
        });

        context.Printers.Add(new Printer { Id = 1, Uuid = Guid.NewGuid(), TeamId = team.Id });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
