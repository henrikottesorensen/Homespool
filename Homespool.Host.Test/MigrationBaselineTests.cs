using System.Collections.Generic;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;

namespace Homespool.Host.Test;

/// <summary>
/// The two rules that let migrations stack from 0.1: the first migration never changes, and the model
/// never runs ahead of the last one.
/// </summary>
/// <remarks>
/// <para>
/// Every database created by 0.1 or later is stamped with <see cref="Baseline"/>, and Entity Framework
/// brings it forward by applying whatever this build carries after that. Regenerating the baseline in
/// place gives every one of those databases an id this build does not carry, which
/// <see cref="MigrationHistoryGuard"/> refuses at start - correctly, and with the deployment down
/// until somebody repairs it by hand. A schema change is therefore a new migration added after the
/// last one.
/// </para>
/// <para>
/// Neither test opens a connection: the migration list comes from the assembly, and the pending-change
/// check compares the model with the snapshot the last migration left.
/// </para>
/// </remarks>
public sealed class MigrationBaselineTests
{
    /// <summary>The migration 0.1 shipped with.</summary>
    private const string Baseline = "20261010083236_InitialCreate";

    /// <summary>
    /// A regenerated or removed baseline changes the first id, and strands every deployed database.
    /// </summary>
    [Fact]
    public void TheFirstMigrationIsTheReleasedBaseline()
    {
        using HomespoolDbContext context = NewContext();

        List<string> carried = [.. context.Database.GetMigrations()];

        carried.Should().NotBeEmpty("a build with no migrations cannot create a database");
        carried[0].Should().Be(Baseline,
                               "every database since 0.1 is stamped with it, so later schema changes are " +
                               "migrations added after it rather than a new first one");
    }

    /// <summary>
    /// A model change that shipped without a migration would leave a deployed database one schema
    /// behind the code with nothing stamped to say so.
    /// </summary>
    [Fact]
    public void TheModelHasNoChangesTheMigrationsDoNotCarry()
    {
        using HomespoolDbContext context = NewContext();

        context.Database.HasPendingModelChanges()
               .Should()
               .BeFalse("a model change ships as a migration of its own: dotnet ef migrations add " +
                        "<Name> --context HomespoolDbContext --project Homespool.Data " +
                        "--startup-project Homespool.Host");
    }

    private static HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite("Data Source=:memory:")
                                                       .Options;

        return new HomespoolDbContext(options);
    }
}
