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
/// <c>journal_mode=WAL</c> is persisted in the database file itself, so setting it repeatedly is
/// harmless. <c>synchronous</c> and <c>busy_timeout</c> are per-connection and must be reapplied
/// each time — which is why this is an interceptor rather than one-off startup code.
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
             PRAGMA journal_mode = WAL;
             PRAGMA synchronous = NORMAL;
             PRAGMA busy_timeout = {_busyTimeoutMilliseconds};
             PRAGMA foreign_keys = ON;
             """);

        command.ExecuteNonQuery();
    }
}
