using System;

using Microsoft.Data.Sqlite;

namespace Homespool.Data;

/// <summary>
/// Holds the one connection that keeps an in-memory telemetry database alive.
/// </summary>
/// <remarks>
/// <para>
/// <b>A SQLite in-memory database exists only while a connection to it is open.</b> Not "is emptied"
/// - ceases to exist, tables and all, the moment the last connection closes. Every other connection
/// here is short-lived and pooled, so without this one an idle moment between requests would silently
/// destroy the schema and the next write would fail against a database with no tables in it. Verified
/// rather than assumed: the same test that confirmed two connections see one shared database
/// confirmed that dropping the last one loses it.
/// </para>
/// <para>
/// <b>Registered as a singleton so the container disposes it at shutdown</b>, and resolved during
/// startup so the database is guaranteed to exist before anything tries to use it.
/// </para>
/// <para>
/// <b>The name is unique per host</b>, which matters more in the test suite than in production: a
/// shared in-memory database is scoped to the process, and the end-to-end suite builds a host per
/// test in one process. A fixed name would have those hosts sharing one telemetry store and reading
/// each other's rows.
/// </para>
/// </remarks>
public sealed class TelemetryKeepAlive : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Opens the connection that brings the database into existence.</summary>
    /// <param name="connectionString">The in-memory connection string, shared with the context.</param>
    public TelemetryKeepAlive(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
