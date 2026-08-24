using System;
using System.IO;

using Microsoft.EntityFrameworkCore;

namespace Homespool.Data;

/// <summary>
/// Writes an empty database carrying this build's schema, so that a deployed database can be
/// compared against it.
/// </summary>
/// <remarks>
/// <para>
/// <c>tools/carry-enrolment.sh</c> compares two databases rather than a database against a model,
/// because on the appliance there is no other way: it has docker and sqlite3 and no .NET SDK, so
/// <c>dotnet ef</c> is not available to produce the reference. The image that will not start is
/// nonetheless <i>right there</i>, and it is the one thing that definitively knows the schema it
/// expects - so it hands over a copy of it.
/// </para>
/// <para>
/// This lives in <c>Homespool.Data</c> rather than beside the other entry-point applets because it
/// is one <c>Migrate()</c> against a path, and because <see cref="MigrationHistoryGuard"/>'s message
/// names the argument. Two files naming the same string is how the instruction in a failure message
/// stops matching the binary it is printed by.
/// </para>
/// </remarks>
public static class SchemaWriter
{
    /// <summary>The entry-point argument that asks for this. Not a server run.</summary>
    /// <remarks>
    /// Note what an <i>older</i> image does with it, which <c>--version</c> documents at length: it
    /// does not recognise the argument, hands it to the host builder and STARTS THE SERVER. Anything
    /// scripting this must bound it in time and check that a database appeared, rather than trusting
    /// the exit code.
    /// </remarks>
    public const string Argument = "--write-schema";

    /// <summary>
    /// Creates <paramref name="path"/> and migrates it to this build's schema.
    /// </summary>
    /// <remarks>
    /// Refuses an existing file rather than migrating into it. The whole point of the output is to be
    /// a known-clean statement of what this build expects, and the obvious slip - pointing it at the
    /// live database to "check" it - would migrate the thing being diagnosed.
    /// </remarks>
    /// <param name="path">Where to write the database. Must not already exist.</param>
    /// <returns>Zero on success, one on failure.</returns>
    public static int Write(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine($"usage: Homespool {Argument} <path>");

            return 1;
        }

        if (File.Exists(path))
        {
            Console.Error.WriteLine($"{Argument}: '{path}' already exists. Refusing to migrate into an " +
                                    "existing database - this writes a reference to compare against, " +
                                    "and pointing it at a real one would change it.");

            return 1;
        }

        try
        {
            DbContextOptions<HomespoolDbContext> options =
                new DbContextOptionsBuilder<HomespoolDbContext>()
                    .UseSqlite($"Data Source={path}")
                    .Options;

            using HomespoolDbContext context = new(options);

            context.Database.Migrate();

            Console.WriteLine(path);

            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"{Argument}: {ex.Message}");

            return 1;
        }
    }
}
