using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Holds down the one property <see cref="DateTimeOffsetToUnixMillisecondsConverter"/> depends on:
/// that the mapping to storage is <b>order-preserving across offsets</b>.
/// </summary>
/// <remarks>
/// <para>
/// This is not a hypothetical. EF's own <c>DateTimeOffsetToBinaryConverter</c> fails exactly here —
/// it stores local <c>Ticks</c> with the offset packed into the low 11 bits, so the same instant in
/// two different offsets produces two different, wrongly-ordered numbers. Sorting and range filters
/// then return wrong rows <i>with no error at all</i>, which is what makes it dangerous.
/// See https://nitratine.net/blog/post/a-warning-for-ef-cores-datetimeoffsettobinaryconverter/.
/// </para>
/// <para>
/// The tests exercise the converter directly <i>and</i> through real SQLite, because a converter that
/// is correct in memory can still be mistranslated: EF applies the converter to the parameter and
/// compares stored representations, so ordering has to survive the round trip into SQL.
/// </para>
/// </remarks>
public sealed class DateTimeOffsetConverterTests : IDisposable
{
    /// <summary>The same seven instants, expressed in a deliberate mix of offsets.</summary>
    /// <remarks>
    /// +13:00 is the offset from the linked article, chosen because it is large enough to push a
    /// value across a day boundary and so exposes ordering bugs that a one-hour offset would hide.
    /// </remarks>
    private static readonly DateTimeOffset[] ChronologicalOrder =
    [
        new DateTimeOffset(2026, 3, 30, 11, 13, 59, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 30, 11, 14, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 30, 11, 14, 1, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(13)),
        new DateTimeOffset(2026, 3, 30, 11, 14, 2, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 30, 11, 14, 3, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(-8)),
        new DateTimeOffset(2026, 3, 30, 11, 14, 4, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(13)),
        new DateTimeOffset(2026, 3, 30, 11, 14, 5, TimeSpan.Zero),
    ];

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-conv-{Guid.NewGuid():N}.db");

    private HSDbContext NewContext()
    {
        DbContextOptions<HSDbContext> options = new DbContextOptionsBuilder<HSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new HSDbContext(options);
    }

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// One instant expressed in three different offsets stores as one value.
    /// </summary>
    /// <remarks>
    /// The property everything else rests on. <c>ToUnixTimeMilliseconds</c> normalises to UTC before
    /// producing the number, so the offset cannot leak into the stored representation - which is
    /// exactly where EF's own <c>DateTimeOffsetToBinaryConverter</c> goes wrong.
    /// </remarks>
    [Fact]
    public void SameInstantInDifferentOffsetsStoresIdenticalValue()
    {
        // Arrange
        Func<DateTimeOffset, long> toStorage = new DateTimeOffsetToUnixMillisecondsConverter()
            .ConvertToProviderExpression.Compile();

        DateTimeOffset utc = new(2026, 3, 30, 11, 14, 1, TimeSpan.Zero);

        // Assert
        toStorage(utc.ToOffset(TimeSpan.FromHours(13))).Should().Be(toStorage(utc));
        toStorage(utc.ToOffset(TimeSpan.FromHours(-8))).Should().Be(toStorage(utc));
    }

    /// <summary>
    /// Chronologically ordered instants stay ordered once converted, even with mixed offsets.
    /// </summary>
    /// <remarks>
    /// Monotonicity is what makes a SQL comparison mean the same thing as a CLR one. EF applies the
    /// converter to the parameter and compares stored representations, so a non-monotonic converter
    /// produces <i>wrong results</i> rather than an error.
    /// </remarks>
    [Fact]
    public void StorageOrderMatchesChronologicalOrderAcrossOffsets()
    {
        // Arrange
        Func<DateTimeOffset, long> toStorage = new DateTimeOffsetToUnixMillisecondsConverter()
            .ConvertToProviderExpression.Compile();

        // Act
        long[] stored = ChronologicalOrder.Select(toStorage).ToArray();

        // Assert
        stored.Should().BeInAscendingOrder("the stored value must be monotonic in the instant, "
                                           + "or SQL comparisons mean something different from CLR ones");
    }

    /// <summary>
    /// The failure mode this converter exists to avoid, asserted rather than described.
    /// </summary>
    [Fact]
    public void EfBuiltInBinaryConverterIsNotOrderPreserving_WhichIsWhyItIsNotUsed()
    {
        // Arrange
        Func<DateTimeOffset, long> broken = new DateTimeOffsetToBinaryConverter()
            .ConvertToProviderExpression.Compile();

        // Act
        long[] stored = ChronologicalOrder.Select(broken).ToArray();

        // Assert
        stored.Should().NotBeInAscendingOrder("if this ever starts passing, EF has fixed "
                                              + "DateTimeOffsetToBinaryConverter and this note can go");
    }

    /// <summary>
    /// The column really is an INTEGER, so the converter is actually being applied.
    /// </summary>
    /// <remarks>
    /// It is registered by convention in <c>ConfigureConventions</c> rather than per property. If that
    /// registration were ever dropped, EF would silently fall back to TEXT and every timestamp
    /// comparison would start throwing at runtime instead of failing here.
    /// </remarks>
    [Fact]
    public async Task SqliteStoresTimestampsAsIntegers()
    {
        // Arrange
        await using HSDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        context.PrusaConnectRegistrations.Add(NewRegistration("fp-1", ChronologicalOrder[0]));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await using System.Data.Common.DbConnection connection = context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "select typeof(TemporaryCodeExpiry) from PrusaConnectRegistrations limit 1";

        // Assert
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))?.ToString().Should().Be("integer");
    }

    /// <summary>
    /// <c>ORDER BY</c> executed by SQLite returns mixed-offset rows in chronological order.
    /// </summary>
    /// <remarks>
    /// Rows are inserted in shuffled order so that a pass cannot come from insertion order. Ordering
    /// correct in memory is not enough - it has to survive the round trip through the provider.
    /// </remarks>
    [Fact]
    public async Task SqlOrderByMatchesChronologicalOrderAcrossOffsets()
    {
        // Arrange
        await using HSDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // insert shuffled, so a passing result cannot come from insertion order
        foreach ((DateTimeOffset stamp, int i) in ChronologicalOrder.Select((s, i) => (s, i)).OrderBy(_ => Guid.NewGuid()))
        {
            context.PrusaConnectRegistrations.Add(NewRegistration($"fp-{i}", stamp));
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        List<DateTimeOffset> sorted = await context.PrusaConnectRegistrations
            .OrderBy(a => a.TemporaryCodeExpiry)
            .Select(a => a.TemporaryCodeExpiry)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        sorted.Should().Equal(ChronologicalOrder.Select(s => s.ToUniversalTime()));
    }

    /// <summary>
    /// A range filter evaluated in SQL keeps rows recorded at a positive offset.
    /// </summary>
    /// <remarks>
    /// The precise failure reported against <c>DateTimeOffsetToBinaryConverter</c>: +13:00 rows fall
    /// out of a window they belong in, silently and with no error. The fixture places two of them
    /// inside the window deliberately.
    /// </remarks>
    [Fact]
    public async Task SqlRangeFilterKeepsRowsAtAPositiveOffset()
    {
        // Arrange
        await using HSDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        foreach ((DateTimeOffset stamp, int i) in ChronologicalOrder.Select((s, i) => (s, i)))
        {
            context.PrusaConnectRegistrations.Add(NewRegistration($"fp-{i}", stamp));
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // the +13:00 rows sit inside this window; the broken converter drops them entirely
        DateTimeOffset from = ChronologicalOrder[1];
        DateTimeOffset to = ChronologicalOrder[5];

        // Act
        List<DateTimeOffset> matched = await context.PrusaConnectRegistrations
            .Where(a => a.TemporaryCodeExpiry >= from && a.TemporaryCodeExpiry <= to)
            .OrderBy(a => a.TemporaryCodeExpiry)
            .Select(a => a.TemporaryCodeExpiry)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        matched.Should().Equal(ChronologicalOrder[1..6].Select(s => s.ToUniversalTime()));
    }

    /// <summary>The phase-4 retention sweep: a bulk server-side delete that must translate.</summary>
    [Fact]
    public async Task BulkDeleteByTimestampTranslatesAndDeletesTheRightRows()
    {
        // Arrange
        await using HSDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        foreach ((DateTimeOffset stamp, int i) in ChronologicalOrder.Select((s, i) => (s, i)))
        {
            context.PrusaConnectRegistrations.Add(NewRegistration($"fp-{i}", stamp));
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        DateTimeOffset cutoff = ChronologicalOrder[3];

        // Act
        int deleted = await context.PrusaConnectRegistrations
            .Where(a => a.TemporaryCodeExpiry < cutoff)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        // Assert
        deleted.Should().Be(3);
        (await context.PrusaConnectRegistrations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(4);
    }

    /// <summary>
    /// Documents the two things this storage format deliberately discards.
    /// </summary>
    /// <remarks>
    /// The original offset is not stored - values return as UTC - and precision truncates to
    /// milliseconds. Neither costs anything here: every timestamp originates from
    /// <c>TimeProvider.System.GetUtcNow()</c>, and the fastest telemetry cadence in the firmware is
    /// 750 ms. Asserted rather than described so the trade-off cannot drift unnoticed.
    /// </remarks>
    [Fact]
    public async Task ValuesRoundTripAsUtcTruncatedToMilliseconds()
    {
        // Arrange
        await using HSDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // deliberately carries sub-millisecond ticks and a non-zero offset
        DateTimeOffset original = new DateTimeOffset(2026, 3, 30, 11, 14, 1, 123, TimeSpan.Zero)
            .AddTicks(4567)
            .ToOffset(TimeSpan.FromHours(13));

        context.PrusaConnectRegistrations.Add(NewRegistration("fp-rt", original));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        // Act
        DateTimeOffset readBack = (await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken)).TemporaryCodeExpiry;

        // Assert
        readBack.Offset.Should().Be(TimeSpan.Zero, "values round-trip as UTC; the offset is not stored");
        readBack.Should().Be(original.ToUniversalTime().AddTicks(-4567), "precision truncates to milliseconds");
        (original - readBack).Should().BeLessThan(TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// A null <c>DateTimeOffset?</c> stays null through a converter declared over the non-nullable type.
    /// </summary>
    /// <remarks>
    /// <c>Invitation.UsedAt</c> is nullable, and the convention registers
    /// <c>Properties&lt;DateTimeOffset&gt;()</c>. EF is expected to lift the conversion over the
    /// nullable form; this confirms it rather than assuming it. (An outstanding invite carries a null
    /// here — this uses <c>Invitation</c> only as a convenient carrier of a nullable timestamp.)
    /// </remarks>
    [Fact]
    public async Task NullableTimestampsSurviveTheConversion()
    {
        // Arrange
        await using HSDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Invitation row = new()
        {
            HashedToken = "hash",
            Email = "someone@example.com",
            CreatedAt = ChronologicalOrder[0],
            ExpiresAt = ChronologicalOrder[0].AddHours(48),
            UsedAt = null,
        };

        // Act
        context.Invitations.Add(row);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        // Assert
        (await context.Invitations.SingleAsync(TestContext.Current.CancellationToken)).UsedAt.Should().BeNull();
    }

    private static PrusaConnectRegistration NewRegistration(string fingerPrint, DateTimeOffset expiry)
    {
        return new()
        {
            FingerPrint = fingerPrint,
            SerialNumber = $"sn-{fingerPrint}",
            TemporaryCode = $"code-{fingerPrint}",
            TemporaryCodeExpiry = expiry,
            CreatedAt = expiry,
        };
    }
}
