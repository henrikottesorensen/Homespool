using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using PrinterService.Data;
using PrinterService.Model.Entities;

namespace PrinterService.Api.Test;

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
/// See https://nitratine.net/blog/post/a-warning-for-ef-cores-datetimeoffsettobinaryconverter/
/// </para>
/// <para>
/// The tests exercise the converter directly <i>and</i> through real SQLite, because a converter that
/// is correct in memory can still be mistranslated: EF applies the converter to the parameter and
/// compares stored representations, so ordering has to survive the round trip into SQL.
/// </para>
/// </remarks>
public sealed class DateTimeOffsetConverterTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-conv-{Guid.NewGuid():N}.db");

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

    private PSDbContext NewContext()
    {
        DbContextOptions<PSDbContext> options = new DbContextOptionsBuilder<PSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new PSDbContext(options);
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

    [Fact]
    public void SameInstantInDifferentOffsetsStoresIdenticalValue()
    {
        Func<DateTimeOffset, long> toStorage = new DateTimeOffsetToUnixMillisecondsConverter()
            .ConvertToProviderExpression.Compile();

        DateTimeOffset utc = new(2026, 3, 30, 11, 14, 1, TimeSpan.Zero);

        toStorage(utc.ToOffset(TimeSpan.FromHours(13))).Should().Be(toStorage(utc));
        toStorage(utc.ToOffset(TimeSpan.FromHours(-8))).Should().Be(toStorage(utc));
    }

    [Fact]
    public void StorageOrderMatchesChronologicalOrderAcrossOffsets()
    {
        Func<DateTimeOffset, long> toStorage = new DateTimeOffsetToUnixMillisecondsConverter()
            .ConvertToProviderExpression.Compile();

        long[] stored = ChronologicalOrder.Select(toStorage).ToArray();

        stored.Should().BeInAscendingOrder("the stored value must be monotonic in the instant, "
                                           + "or SQL comparisons mean something different from CLR ones");
    }

    /// <summary>
    /// The failure mode this converter exists to avoid, asserted rather than described.
    /// </summary>
    [Fact]
    public void EfBuiltInBinaryConverterIsNotOrderPreserving_WhichIsWhyItIsNotUsed()
    {
        Func<DateTimeOffset, long> broken = new DateTimeOffsetToBinaryConverter()
            .ConvertToProviderExpression.Compile();

        long[] stored = ChronologicalOrder.Select(broken).ToArray();

        stored.Should().NotBeInAscendingOrder("if this ever starts passing, EF has fixed "
                                              + "DateTimeOffsetToBinaryConverter and this note can go");
    }

    [Fact]
    public async Task SqliteStoresTimestampsAsIntegers()
    {
        await using PSDbContext context = NewContext();
        await context.Database.MigrateAsync();

        context.PrusaConnectAuthentication.Add(NewAuth("fp-1", ChronologicalOrder[0]));
        await context.SaveChangesAsync();

        await using System.Data.Common.DbConnection connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "select typeof(TemporaryCodeExpiry) from PrusaConnectAuthentication limit 1";

        (await command.ExecuteScalarAsync())?.ToString().Should().Be("integer");
    }

    [Fact]
    public async Task SqlOrderByMatchesChronologicalOrderAcrossOffsets()
    {
        await using PSDbContext context = NewContext();
        await context.Database.MigrateAsync();

        // insert shuffled, so a passing result cannot come from insertion order
        foreach ((DateTimeOffset stamp, int i) in ChronologicalOrder.Select((s, i) => (s, i)).OrderBy(_ => Guid.NewGuid()))
        {
            context.PrusaConnectAuthentication.Add(NewAuth($"fp-{i}", stamp));
        }

        await context.SaveChangesAsync();

        List<DateTimeOffset> sorted = await context.PrusaConnectAuthentication
            .OrderBy(a => a.TemporaryCodeExpiry)
            .Select(a => a.TemporaryCodeExpiry)
            .ToListAsync();

        sorted.Should().Equal(ChronologicalOrder.Select(s => s.ToUniversalTime()));
    }

    [Fact]
    public async Task SqlRangeFilterKeepsRowsAtAPositiveOffset()
    {
        await using PSDbContext context = NewContext();
        await context.Database.MigrateAsync();

        foreach ((DateTimeOffset stamp, int i) in ChronologicalOrder.Select((s, i) => (s, i)))
        {
            context.PrusaConnectAuthentication.Add(NewAuth($"fp-{i}", stamp));
        }

        await context.SaveChangesAsync();

        // the +13:00 rows sit inside this window; the broken converter drops them entirely
        DateTimeOffset from = ChronologicalOrder[1];
        DateTimeOffset to = ChronologicalOrder[5];

        List<DateTimeOffset> matched = await context.PrusaConnectAuthentication
            .Where(a => a.TemporaryCodeExpiry >= from && a.TemporaryCodeExpiry <= to)
            .OrderBy(a => a.TemporaryCodeExpiry)
            .Select(a => a.TemporaryCodeExpiry)
            .ToListAsync();

        matched.Should().Equal(ChronologicalOrder[1..6].Select(s => s.ToUniversalTime()));
    }

    /// <summary>The phase-4 retention sweep: a bulk server-side delete that must translate.</summary>
    [Fact]
    public async Task BulkDeleteByTimestampTranslatesAndDeletesTheRightRows()
    {
        await using PSDbContext context = NewContext();
        await context.Database.MigrateAsync();

        foreach ((DateTimeOffset stamp, int i) in ChronologicalOrder.Select((s, i) => (s, i)))
        {
            context.PrusaConnectAuthentication.Add(NewAuth($"fp-{i}", stamp));
        }

        await context.SaveChangesAsync();

        DateTimeOffset cutoff = ChronologicalOrder[3];

        int deleted = await context.PrusaConnectAuthentication
            .Where(a => a.TemporaryCodeExpiry < cutoff)
            .ExecuteDeleteAsync();

        deleted.Should().Be(3);
        (await context.PrusaConnectAuthentication.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task ValuesRoundTripAsUtcTruncatedToMilliseconds()
    {
        await using PSDbContext context = NewContext();
        await context.Database.MigrateAsync();

        // deliberately carries sub-millisecond ticks and a non-zero offset
        DateTimeOffset original = new DateTimeOffset(2026, 3, 30, 11, 14, 1, 123, TimeSpan.Zero)
            .AddTicks(4567)
            .ToOffset(TimeSpan.FromHours(13));

        context.PrusaConnectAuthentication.Add(NewAuth("fp-rt", original));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        DateTimeOffset readBack = (await context.PrusaConnectAuthentication.SingleAsync()).TemporaryCodeExpiry;

        readBack.Offset.Should().Be(TimeSpan.Zero, "values round-trip as UTC; the offset is not stored");
        readBack.Should().Be(original.ToUniversalTime().AddTicks(-4567), "precision truncates to milliseconds");
        (original - readBack).Should().BeLessThan(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task NullableTimestampsSurviveTheConversion()
    {
        await using PSDbContext context = NewContext();
        await context.Database.MigrateAsync();

        PrusaConnectAuthenticationData row = NewAuth("fp-null", ChronologicalOrder[0]);
        row.TokenCreatedAt = null;
        context.PrusaConnectAuthentication.Add(row);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        (await context.PrusaConnectAuthentication.SingleAsync()).TokenCreatedAt.Should().BeNull();
    }

    private static PrusaConnectAuthenticationData NewAuth(string fingerPrint, DateTimeOffset expiry) => new()
    {
        FingerPrint = fingerPrint,
        SerialNumber = $"sn-{fingerPrint}",
        TemporaryCode = $"code-{fingerPrint}",
        TemporaryCodeExpiry = expiry,
        CreatedAt = expiry,
    };
}
