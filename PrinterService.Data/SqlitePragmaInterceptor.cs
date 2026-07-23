using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PrinterService.Data;

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
/// It is now applied once by <see cref="DataServiceCollectionExtensions.MigratePrinterServiceData"/>.
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

        command.CommandText = string.Create(CultureInfo.InvariantCulture,
            $"""
             PRAGMA synchronous = NORMAL;
             PRAGMA busy_timeout = {_busyTimeoutMilliseconds};
             """);

        command.ExecuteNonQuery();
    }
}
