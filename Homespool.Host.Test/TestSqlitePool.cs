using Microsoft.Data.Sqlite;

namespace Homespool.Host.Test;

/// <summary>
/// Closes the pooled connections to one test's SQLite file, and nobody else's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never <c>SqliteConnection.ClearAllPools</c>, which is banned for it.</b> It empties every
/// pool in the process, and xUnit runs test classes in parallel, so it disposes handles that another
/// class's connection is opening at that moment. That surfaces as an <see cref="System.ObjectDisposedException"/>
/// on <c>SQLitePCL.sqlite3</c> from <see cref="SqliteConnection.Open"/> in some unrelated test - and,
/// when the handle goes mid-call, as a <see cref="System.NullReferenceException"/> in SQLite's
/// function-callback bridge that aborts the test host outright.
/// </para>
/// <para>
/// <b>The connection string has to match the test's exactly</b>, because a pool is keyed by the
/// string. Every caller builds <c>Data Source=</c> and the path, as <see cref="TestTelemetryContext"/>
/// does, and EF opens with the configured string unchanged.
/// </para>
/// </remarks>
internal static class TestSqlitePool
{
    /// <summary>Closes the idle pooled connections to the SQLite file at <paramref name="databasePath"/>.</summary>
    public static void Release(string databasePath)
    {
        using SqliteConnection connection = new($"Data Source={databasePath}");
        SqliteConnection.ClearPool(connection);
    }
}
