using System;
using System.Collections.Generic;

namespace Homespool.Data;

/// <summary>
/// The database was migrated by a build carrying a different set of migrations than this one.
/// </summary>
/// <remarks>
/// <para>
/// Carries both id sets rather than only a sentence, so a caller can say which build stamped the
/// database and which one is refusing to start. The sentence is built from them and is the thing an
/// operator actually reads, but a test asserting on prose is a test that breaks when the prose is
/// improved.
/// </para>
/// <para>
/// See <see cref="MigrationHistoryGuard"/> for the two shapes that raise this and why neither is
/// something Entity Framework can report intelligibly on its own.
/// </para>
/// </remarks>
public class MigrationHistoryMismatchException : Exception
{
    public MigrationHistoryMismatchException()
    {
        StampedIds = [];
        BuildMigrationIds = [];
    }

    public MigrationHistoryMismatchException(string message)
        : base(message)
    {
        StampedIds = [];
        BuildMigrationIds = [];
    }

    public MigrationHistoryMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
        StampedIds = [];
        BuildMigrationIds = [];
    }

    public MigrationHistoryMismatchException(string message,
                                             IReadOnlyList<string> stampedIds,
                                             IReadOnlyList<string> buildMigrationIds)
        : base(message)
    {
        StampedIds = stampedIds;
        BuildMigrationIds = buildMigrationIds;
    }

    /// <summary>
    /// The migration ids <c>__EFMigrationsHistory</c> claims are applied. Empty when the database has
    /// tables but no history at all.
    /// </summary>
    public IReadOnlyList<string> StampedIds { get; }

    /// <summary>The migration ids this build carries in its assembly.</summary>
    public IReadOnlyList<string> BuildMigrationIds { get; }
}
