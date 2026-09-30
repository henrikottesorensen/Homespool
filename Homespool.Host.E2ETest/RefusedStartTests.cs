using System;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Data.Sqlite;

using Homespool.Data;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A host that refuses to start tells the test that started it why.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this guards is Program rethrowing when it is not the process.</b> The factory discards
/// Main's return value, so a refusal that is only logged and returned reaches the test as "the entry
/// point exited without ever building an IHost", as a server that "has not been started", or as an
/// <see cref="ObjectDisposedException"/> from a host that looked started - depending only on how far
/// startup got, and never with the reason. Or not at all: a host refused after it was built but before
/// it ran is never disposed, so <c>factory.Server</c> returns a server that never started, and the
/// test below then fails with no exception thrown.
/// </para>
/// <para>
/// A stamped migration history is the refusal used because it needs nothing but the database: no
/// ports, no certificates. And it happens between building the host and running it, the stage whose
/// failure otherwise looks like a successful start.
/// </para>
/// </remarks>
public sealed class RefusedStartTests : IDisposable
{
    private const string ForeignMigration = "20990101000000_NotThisBuild";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("refused-start");

    public void Dispose()
    {
        _scratch.Dispose();
    }

    /// <summary>
    /// A database stamped by another build is refused, and the refusal itself reaches the test, with
    /// the id it was refused over.
    /// </summary>
    [Fact]
    public async Task ARefusalReachesTheTestWithItsReason()
    {
        await StampAsync(ForeignMigration);

        using HomespoolFactory factory = new(_scratch);

        Action start = () => _ = factory.Server;

        start.Should().Throw<MigrationHistoryMismatchException>()
             .Which.StampedIds.Should().Equal([ForeignMigration], "that is what the database says was applied");
    }

    /// <summary>Gives the scratch database a migration history naming one migration this build does not carry.</summary>
    private async Task StampAsync(string migrationId)
    {
        await using SqliteConnection connection = new(_scratch.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL); " +
                              "INSERT INTO \"__EFMigrationsHistory\" VALUES ($id, '10.0.0');";
        command.Parameters.AddWithValue("$id", migrationId);

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
