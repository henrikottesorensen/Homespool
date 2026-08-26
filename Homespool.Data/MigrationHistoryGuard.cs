using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

using Microsoft.EntityFrameworkCore;

namespace Homespool.Data;

/// <summary>
/// Refuses to start against a database some other build of Homespool migrated, and says what to do
/// about it.
/// </summary>
/// <remarks>
/// <para>
/// This project regenerates its single migration in place while pre-release rather than stacking
/// migrations, so an upgraded image routinely carries a migration id the deployed database has never
/// been stamped with. Entity Framework reads that as one pending migration, runs its
/// <c>CREATE TABLE</c> and dies on <c>table "AspNetRoles" already exists</c> - an exception that
/// names neither the cause nor the remedy, and which has stranded a real appliance four times.
/// </para>
/// <para>
/// Both halves of the comparison are already in hand at that moment: the stamped ids come from
/// <c>__EFMigrationsHistory</c> and the carried ones from the assembly. Making the failure diagnose
/// itself therefore costs one comparison, and it covers shapes nobody has met yet - a stamped id
/// from a <i>newer</i> build, on a database rolled forward and then downgraded, reads the same way
/// here and is equally fatal.
/// </para>
/// </remarks>
public static class MigrationHistoryGuard
{
    /// <summary>
    /// The remedy, named in both messages.
    /// </summary>
    /// <remarks>
    /// Names <c>check</c> before <c>upgrade</c> deliberately, and both before <c>adopt</c>. The rule
    /// the tool exists to enforce is compare-then-stamp: a stamp is a claim about a schema, and an
    /// operator who reaches for the stamp first is the one who makes that claim false.
    /// </remarks>
    private const string RepairInstructions = """
Produce a database this build created, then compare the deployed one against it:

  Homespool --write-schema /tmp/reference.sqlite
  carry-enrolment.sh check   <database> /tmp/reference.sqlite

carry-enrolment.sh lives in the repository, under tools/, and is NOT part of this image - copy it
to wherever you are running this. sqlite3, which it needs, IS in the image. In docker, both steps
are one command each against the stopped service:

  docker compose run --rm --no-deps homespool --write-schema /app/data/reference.sqlite
  docker compose run --rm --no-deps -v ./carry-enrolment.sh:/ce.sh:ro --entrypoint bash homespool \
      /ce.sh check /app/data/Homespool.Sqlite /app/data/reference.sqlite

If check reports only additive differences - new tables, new nullable columns - then 'upgrade'
applies them and stamps the history, in one transaction. It refuses anything else, naming what it
found. A column whose TYPE changed cannot be stamped across at all; that case needs 'adopt', which
carries the enrolled printer into a database this build created.

Delete the reference database afterwards. And stop the service first: the tool refuses a database
something else holds, which is the check that stops a repair racing a restart loop.
""";

    /// <summary>
    /// Throws <see cref="MigrationHistoryMismatchException"/> if the database was stamped by a build
    /// carrying different migrations, or holds tables with no history at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately checked whether or not <see cref="StorageOptions.AutoMigrate"/> is set. That flag
    /// says who applies schema changes; it does not say the schema may be the wrong one. An operator
    /// who has taken migrations into their own hands still needs to be told that the file under this
    /// process is not the file this build was built against, and with the flag off nothing else would
    /// ever notice - the application would simply start and fail later, on a column.
    /// </para>
    /// <para>
    /// An empty database passes: no history and no tables is a first run, which is the overwhelmingly
    /// common case and must stay silent.
    /// </para>
    /// </remarks>
    /// <param name="context">The context whose database is about to be migrated.</param>
    public static void Verify(HomespoolDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<string> stamped = [.. context.Database.GetAppliedMigrations()];
        List<string> carried = [.. context.Database.GetMigrations()];

        // Ids the database claims to have applied that this build has no migration for. This is the
        // regenerate-in-place fingerprint: the old id is not "an older version of" the new one, it is
        // a file that no longer exists, so EF has nothing to compare it against and treats the
        // replacement as never applied.
        List<string> unknown = [.. stamped.Where(id => !carried.Contains(id, StringComparer.Ordinal))];

        if (unknown.Count > 0)
        {
            Refuse(BuildMismatchMessage(context, stamped, carried, unknown), stamped, carried);
        }

        // The other route to the same CREATE TABLE failure, and the reason this is not simply "are the
        // two id lists equal": a database whose history table was dropped, or restored from a dump
        // that omitted it, has no stamped ids to disagree with. Nothing is unknown, everything is
        // pending, and every table already exists.
        if (stamped.Count == 0 && CountUserTables(context) > 0)
        {
            Refuse(BuildNoHistoryMessage(context, carried), stamped, carried);
        }
    }

    /// <summary>
    /// Writes the explanation where a person will read it, then throws it where the host will.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both, and the plain write is not redundant. Serilog is configured with the compact JSON
    /// formatter, so an exception message reaches <c>docker compose logs</c> as one line with every
    /// newline escaped to <c>\n</c> - and this message is fifteen lines of instructions whose entire
    /// value is being read by eye, by somebody whose appliance is down. A wall of escaped text is
    /// only marginally better than the EF stack trace it replaces.
    /// </para>
    /// <para>
    /// The same argument as <c>--version</c>: what is asked when the application cannot start must
    /// not depend on the application having started. The logger here is configured, but its format is
    /// chosen for machines.
    /// </para>
    /// </remarks>
    [DoesNotReturn]
    private static void Refuse(string message, IReadOnlyList<string> stamped, IReadOnlyList<string> carried)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();

        throw new MigrationHistoryMismatchException(message, stamped, carried);
    }

    /// <summary>
    /// Tables that are neither SQLite's own bookkeeping nor the migration history table.
    /// </summary>
    /// <remarks>
    /// Raw SQL rather than a relational abstraction because there is not one: EF can say whether the
    /// history table exists, but "does this database hold anything at all" has no provider-neutral
    /// question. SQLite is the only provider this application is ever configured against.
    /// </remarks>
    private static int CountUserTables(HomespoolDbContext context)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool wasClosed = connection.State != ConnectionState.Open;

        try
        {
            if (wasClosed)
            {
                connection.Open();
            }

            using DbCommand command = connection.CreateCommand();

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master " +
                                  "WHERE type = 'table' AND name NOT LIKE 'sqlite_%' " +
                                  "AND name <> '__EFMigrationsHistory';";

            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            if (wasClosed && connection.State == ConnectionState.Open)
            {
                connection.Close();
            }
        }
    }

    private static string BuildMismatchMessage(HomespoolDbContext context,
                                               IReadOnlyList<string> stamped,
                                               IReadOnlyList<string> carried,
                                               IReadOnlyList<string> unknown)
    {
        return $"""
                The database at '{DataSourceOf(context)}' was migrated by a different build of Homespool.

                  stamped in the database:  {Join(stamped)}
                  carried by this build:    {Join(carried)}
                  stamped but not carried:  {Join(unknown)}

                Starting anyway would apply this build's migration over tables that already exist and
                fail on the first CREATE TABLE. That is the error this replaces.

                DO NOT DELETE THE DATABASE. It holds the enrolled printers, and a printer's credential
                cannot be replaced without somebody standing at the machine.

                {RepairInstructions}
                """;
    }

    private static string BuildNoHistoryMessage(HomespoolDbContext context, IReadOnlyList<string> carried)
    {
        return $"""
                The database at '{DataSourceOf(context)}' has tables but no migration history, so
                nothing records which schema it holds.

                  carried by this build:    {Join(carried)}

                Every migration reads as pending, so starting would apply this build's migration over
                tables that already exist and fail on the first CREATE TABLE.

                DO NOT DELETE THE DATABASE. It holds the enrolled printers, and a printer's credential
                cannot be replaced without somebody standing at the machine.

                {RepairInstructions}
                """;
    }

    private static string DataSourceOf(HomespoolDbContext context)
    {
        return context.Database.GetDbConnection().DataSource;
    }

    private static string Join(IReadOnlyList<string> ids)
    {
        return ids.Count == 0 ? "(none)" : string.Join(", ", ids);
    }
}
