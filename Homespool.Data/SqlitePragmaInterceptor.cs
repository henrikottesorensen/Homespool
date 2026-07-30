using System;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Homespool.Data;

/// <summary>
/// Applies SQLite pragmas to every connection as it opens.
/// </summary>
/// <remarks>
/// <para>
/// <c>synchronous</c> and <c>busy_timeout</c> are per-connection and must be reapplied on every
/// open — which is why this is an interceptor. Neither is a write, so both are safe on a
/// read-only connection.
/// </para>
/// <para>
/// <b><c>journal_mode</c> is deliberately NOT set here.</b> It is persisted in the database file,
/// so it only needs setting once, and issuing it per connection was a latent startup crash:
/// <c>Migrator.Migrate()</c> calls <c>SqliteDatabaseCreator.Exists()</c>, which opens the
/// connection <i>read-only</i> so that testing for existence cannot create a file. Setting WAL on
/// a database not already in WAL mode is a write, so it failed there with SQLITE_READONLY —
/// invisibly, because on a database already in WAL mode the same statement is a no-op read.
/// Any non-WAL database, most realistically a restored backup, crashed the service at boot.
/// It is now applied once by <see cref="DataServiceCollectionExtensions.MigrateHomespoolData"/>.
/// </para>
/// <para>
/// WAL lets readers proceed while the telemetry writer commits, which matters because the writer
/// commits continuously. <c>synchronous=NORMAL</c> under WAL risks losing the last commits on
/// power loss but not corruption, which is the right trade for telemetry.
/// </para>
/// <para>
/// <b>WAL requires real local disk.</b> Its locking is unreliable on NFS/CIFS/SMB, so the database
/// must not live on a network share — see AGENT-NOTES §5.
/// </para>
/// </remarks>
public class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private readonly int _busyTimeoutMilliseconds;

    /// <summary>Creates the interceptor with the writer's configured busy budget.</summary>
    /// <param name="busyTimeoutMilliseconds">
    /// The total a blocked writer should wait, from <see cref="StorageOptions.BusyTimeoutMilliseconds"/>.
    /// Half of it is issued as the pragma - see <see cref="ApplyPragmas"/> for why.
    /// </param>
    public SqlitePragmaInterceptor(int busyTimeoutMilliseconds)
    {
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);

        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection,
                                                     ConnectionEndEventData eventData,
                                                     CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    // CA2100: SQLite does not accept bound parameters in PRAGMA statements, so the timeout has
    // to be interpolated. It is an int from configuration, never user input, so it cannot carry
    // a payload.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
                     Justification = "PRAGMA cannot be parameterised; the only interpolated value is an int from config.")]
    private void ApplyPragmas(DbConnection connection)
    {
        using DbCommand command = connection.CreateCommand();

        // Half the configured budget, deliberately, because this pragma is not the bound - it is the
        // *granularity* of one. Microsoft.Data.Sqlite catches the SQLITE_BUSY this produces and
        // retries the command itself until CommandTimeout, so the two layers compound: with both set
        // to the same 5,000 ms, a blocked command took ~10 s, twice what the option documents.
        // Measured 2026-07-30, and it was what kept a shutdown at ~30 s even after the command
        // timeout was wired up (notes/fake-printer-harness.md). Halving it lets at most two waits
        // fit inside the caller's budget, so the total lands on the configured value rather than
        // double it, and callers that want a tighter bound (TelemetryWriter's shutdown flush) can
        // still lower both together on their own connection.
        int granularity = Math.Max(_busyTimeoutMilliseconds / 2, 1);

        command.CommandText = string.Create(CultureInfo.InvariantCulture,
            $"""
             PRAGMA synchronous = NORMAL;
             PRAGMA busy_timeout = {granularity};
             """);

        command.ExecuteNonQuery();
    }
}
