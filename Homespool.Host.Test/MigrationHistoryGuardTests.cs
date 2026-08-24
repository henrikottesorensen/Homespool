using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;

namespace Homespool.Host.Test;

/// <summary>
/// The startup refusal that replaces <c>table "AspNetRoles" already exists</c>.
/// </summary>
/// <remarks>
/// <para>
/// The failure being guarded against is not hypothetical and not rare: regenerating the single
/// migration in place is this project's convention while pre-release, and a deployed appliance has
/// met the consequence four times. What the guard buys is not preventing it - only stacking
/// migrations does that - but making it say what happened and what to run.
/// </para>
/// <para>
/// So the assertions come in pairs: that it throws on the shape, and that what it throws carries the
/// two id sets. A message is prose and will be improved; the ids are the thing an operator needs and
/// a caller can read.
/// </para>
/// </remarks>
public sealed class MigrationHistoryGuardTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-guard-{Guid.NewGuid():N}.db");

    /// <summary>
    /// The overwhelmingly common case, and the one that must stay silent: a database this build
    /// migrated itself.
    /// </summary>
    [Fact]
    public async Task ADatabaseThisBuildMigratedPasses()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        Action verify = () => MigrationHistoryGuard.Verify(context);

        verify.Should().NotThrow("a database carrying exactly this build's migration is the normal case");
    }

    /// <summary>A first run: no file, no history, no tables.</summary>
    [Fact]
    public void AnEmptyDatabasePasses()
    {
        using HomespoolDbContext context = NewContext();

        Action verify = () => MigrationHistoryGuard.Verify(context);

        verify.Should().NotThrow("first run has nothing to disagree with, and must not be refused");
    }

    /// <summary>
    /// The regenerate-in-place shape, and today's incident: the schema is whatever it is, and the
    /// stamped id names a migration this build does not have.
    /// </summary>
    [Fact]
    public async Task ADatabaseStampedByAnotherBuildIsRefused()
    {
        await using (HomespoolDbContext context = await MigratedContextAsync())
        {
            await RestampAsync(context, "20260820162112_InitialCreate");
        }

        await using HomespoolDbContext reopened = NewContext();

        Action verify = () => MigrationHistoryGuard.Verify(reopened);

        verify.Should()
              .Throw<MigrationHistoryMismatchException>(
                  "EF would treat the migration it does carry as pending and run its CREATE TABLE "
                  + "over tables that already exist");
    }

    /// <summary>
    /// What the refusal carries, which is the half a caller can act on.
    /// </summary>
    [Fact]
    public async Task TheRefusalNamesBothTheStampedIdAndTheCarriedOne()
    {
        List<string> carried;

        await using (HomespoolDbContext context = await MigratedContextAsync())
        {
            carried = [.. context.Database.GetMigrations()];
            await RestampAsync(context, "20260820162112_InitialCreate");
        }

        await using HomespoolDbContext reopened = NewContext();

        MigrationHistoryMismatchException thrown =
            Assert.Throws<MigrationHistoryMismatchException>(() => MigrationHistoryGuard.Verify(reopened));

        thrown.StampedIds.Should().BeEquivalentTo(["20260820162112_InitialCreate"], "that is what the file says");
        thrown.BuildMigrationIds.Should().BeEquivalentTo(carried, "and that is what this build has");
    }

    /// <summary>
    /// The message is what an operator reads at three in the morning, so it must contain the two ids
    /// and the name of the tool. Asserted loosely on purpose - the wording around them is free to
    /// change, the facts are not.
    /// </summary>
    [Fact]
    public async Task TheMessageNamesTheIdsAndTheTool()
    {
        string carried;

        await using (HomespoolDbContext context = await MigratedContextAsync())
        {
            carried = context.Database.GetMigrations().Single();
            await RestampAsync(context, "20260820162112_InitialCreate");
        }

        await using HomespoolDbContext reopened = NewContext();

        MigrationHistoryMismatchException thrown =
            Assert.Throws<MigrationHistoryMismatchException>(() => MigrationHistoryGuard.Verify(reopened));

        thrown.Message.Should().Contain("20260820162112_InitialCreate", "the operator needs the id that is stamped");
        thrown.Message.Should().Contain(carried, "and the one this build carries");
        thrown.Message.Should().Contain("carry-enrolment.sh", "and the tool that repairs it");
        thrown.Message.Should().Contain(SchemaWriter.Argument, "which needs a reference database first");
        thrown.Message.Should().Contain("DO NOT DELETE", "because deleting it strands the enrolled printer");
    }

    /// <summary>
    /// The second route to the same failure: tables, but nothing recording which schema they are.
    /// </summary>
    /// <remarks>
    /// Reached by a restore that dropped the history table, or a <c>.dump</c> reimport that omitted
    /// it. Nothing is stamped, so nothing disagrees - every migration reads as pending and every
    /// table already exists.
    /// </remarks>
    [Fact]
    public async Task ADatabaseWithTablesButNoHistoryIsRefused()
    {
        await using (HomespoolDbContext context = await MigratedContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\";",
                                                      TestContext.Current.CancellationToken);
        }

        await using HomespoolDbContext reopened = NewContext();

        MigrationHistoryMismatchException thrown =
            Assert.Throws<MigrationHistoryMismatchException>(() => MigrationHistoryGuard.Verify(reopened));

        thrown.StampedIds.Should().BeEmpty("there is no history to read");
        thrown.Message.Should().Contain("no migration history", "which is a different sentence to the mismatch");
    }

    /// <summary>
    /// The applet that produces the reference database the repair tool compares against.
    /// </summary>
    [Fact]
    public async Task WriteSchemaProducesAMigratedDatabase()
    {
        int status = SchemaWriter.Write(_databasePath);

        status.Should().Be(0);

        await using HomespoolDbContext context = NewContext();

        IEnumerable<string> applied =
            await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);

        applied.Should()
               .BeEquivalentTo(context.Database.GetMigrations(),
                               "the reference has to be exactly what this build would create");
    }

    /// <summary>
    /// Refusing an existing file, because the obvious slip is pointing this at the live database.
    /// </summary>
    [Fact]
    public async Task WriteSchemaRefusesAnExistingFile()
    {
        await using (HomespoolDbContext context = await MigratedContextAsync())
        {
            await RestampAsync(context, "20260820162112_InitialCreate");
        }

        SqliteConnection.ClearAllPools();

        SchemaWriter.Write(_databasePath).Should().Be(1, "migrating into it is what this must never do");

        await using HomespoolDbContext reopened = NewContext();

        IEnumerable<string> applied =
            await reopened.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);

        applied.Should()
                .BeEquivalentTo(["20260820162112_InitialCreate"], "so the database it was pointed at is untouched");
    }

    private static async Task RestampAsync(HomespoolDbContext context, string migrationId)
    {
        await context.Database.ExecuteSqlAsync(
            $"UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = {migrationId}",
            TestContext.Current.CancellationToken);
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
