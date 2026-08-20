using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using Homespool.Data;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Every enum column holds the member's name, not its position.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule, and why it is worth a test of its own.</b> An integer discriminator makes an enum's
/// declaration order part of the schema, silently: insert a member above another and every stored
/// row means something else, with nothing to fail. None of these enums mirrors a wire protocol —
/// <see cref="PrinterStatus"/> says so in its own summary — so nothing outside this repository pins
/// the order, and the only thing that ever did was the database.
/// </para>
/// <para>
/// The rule was applied one column at a time and was half-finished for a while, which is the reason
/// for <see cref="EveryEnumColumnIsStoredAsText"/>: it reads the model rather than a list written by
/// hand, so a new entity that forgets the conversion fails here instead of being noticed by somebody
/// reading rows a year later.
/// </para>
/// </remarks>
public sealed class EnumStorageTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-enum-storage-{Guid.NewGuid():N}.db");

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
    /// The invariant, read from the model so it covers columns nobody has written yet.
    /// </summary>
    /// <remarks>
    /// Driven off <see cref="DbContext.Model"/> rather than a hand-written list, because a hand-written
    /// list is exactly what left four of these as integers for as long as it did. A new enum property
    /// on any entity is covered the moment it exists.
    /// </remarks>
    [Fact]
    public void EveryEnumColumnIsStoredAsText()
    {
        // Arrange
        using HomespoolDbContext context = NewContext();

        // Act
        List<string> stored = [];

        foreach (IEntityType entity in context.Model.GetEntityTypes())
        {
            foreach (IProperty property in entity.GetProperties())
            {
                Type bare = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

                if (!bare.IsEnum)
                {
                    continue;
                }

                Type? provider = property.GetTypeMapping().Converter?.ProviderClrType;

                stored.Add($"{entity.ShortName()}.{property.Name} -> {provider?.Name ?? property.ClrType.Name}");
            }
        }

        // Assert
        stored.Should().NotBeEmpty("if this finds nothing, the model is not being built and the test proves nothing");

        stored.Should().OnlyContain(line => line.EndsWith(" -> String", StringComparison.Ordinal),
                                    "an enum stored as a number makes its declaration order part of the schema");
    }

    /// <summary>
    /// The names really reach the file, so the conversion is applied rather than merely configured.
    /// </summary>
    /// <remarks>
    /// The model test above would still pass if EF built the mapping and the provider ignored it. This
    /// one reads the bytes back through raw SQL, which is also the way somebody would meet these values
    /// in the first place — the whole point of storing them this way.
    /// </remarks>
    [Theory]
    [InlineData("Printers", "Type", "PrusaConnect")]
    [InlineData("Printers", "Status", "Attention")]
    [InlineData("PrinterEvents", "EventType", "StateChanged")]
    [InlineData("PrinterEvents", "Status", "Attention")]
    [InlineData("PrinterLiveStates", "Status", "Printing")]
    [InlineData("TelemetrySamples", "Status", "Printing")]
    [InlineData("PrintFiles", "MetadataState", "Unreadable")]
    [InlineData("PrintFilesOnPrinters", "HoldReason", "InsufficientSpace")]
    [InlineData("PrintJobs", "State", "Stopped")]
    public async Task AnEnumColumnHoldsTheMemberName(string table, string column, string expected)
    {
        // Arrange
        await using HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedOneOfEverythingAsync(context);

        // Act
        //
        // SQLite's typeof() and the value in one read, so a column that is text but holds the wrong
        // words fails as loudly as one that is still an integer.
        //
        // Composed rather than passed as an interpolated argument, which is what EF1002 looks for: an
        // identifier cannot be a parameter, so the raw API is the only one that can ask this question.
        // Both names are this test's own InlineData constants and neither reaches it from a caller.
        string sql = string.Concat(
            "SELECT typeof(", column, ") || ' ' || ", column, " AS Value FROM ", table, " LIMIT 1");

        string stored = await context.Database
                                     .SqlQueryRaw<string>(sql)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        // Assert
        stored.Should().Be($"text {expected}",
                           $"{table}.{column} must hold the member's name - an integer there would make the "
                           + "enum's declaration order part of the schema");
    }

    /// <summary>
    /// One row in every table that carries an enum, with a distinguishable value in each.
    /// </summary>
    /// <remarks>
    /// Deliberately not the default member anywhere: <c>Undefined</c> is zero, so a column that had
    /// silently stayed an integer would still read back as the right name through EF and only the raw
    /// value would differ. Values that are not zero make the failure visible either way.
    /// </remarks>
    private static async Task SeedOneOfEverythingAsync(HomespoolDbContext context)
    {
        const string email = "owner@example.com";

        context.Users.Add(new HSUser(email)
        {
            Id = 1,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        });

        Team team = new() { Name = "Workshop" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            TeamId = team.Id,
            Type = PrinterType.PrusaConnect,
            Status = PrinterStatus.Attention,
        };

        PrintFile file = new()
        {
            UserId = 1,
            Name = "bracket.gcode",
            Size = 4096,
            UploadedAt = DateTimeOffset.UtcNow,
            MetadataState = PrintFileMetadataState.Unreadable,
        };

        context.Printers.Add(printer);
        context.PrintFiles.Add(file);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrinterEvents.Add(new PrinterEvent
        {
            PrinterId = printer.Id,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = PrinterEventType.StateChanged,
            Status = PrinterStatus.Attention,
        });

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printer.Id,
            Status = PrinterStatus.Printing,
        });

        context.TelemetrySamples.Add(new TelemetrySample
        {
            PrinterId = printer.Id,
            Timestamp = DateTimeOffset.UtcNow,
            Status = PrinterStatus.Printing,
        });

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = printer.Id,
            PrintFileId = file.Id,
            HoldReason = PrintHoldReason.InsufficientSpace,
            BlockedAt = DateTimeOffset.UtcNow,
        });

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = printer.Id,
            FileName = "bracket.gcode",
            State = PrintState.Stopped,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }
}
