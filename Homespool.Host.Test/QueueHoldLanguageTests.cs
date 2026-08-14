using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Localisation;
using Homespool.Host.Queue;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// A hold recorded by a background loop, read by a person in their own language.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the case that could not be solved by translating a string.</b> The queue advancer
/// writes the hold from a timer, with no request and no reader, and the row outlives the moment it
/// was written — so there is no point at which a language could have been chosen. The column
/// records what happened; these tests are about the words being chosen later, and correctly.
/// </para>
/// <para>
/// Before this, <c>BlockedReason</c> held finished English and the free-space check recognised its
/// own holds by matching its own opening words. Translating that sentence would have left a Danish
/// string in the column and an English prefix in the comparison, so the hold would never have
/// lifted — the queue stays stopped after somebody frees the space. That is the failure the last
/// test here exists for.
/// </para>
/// </remarks>
public sealed class QueueHoldLanguageTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-queue-hold-{Guid.NewGuid():N}.db");

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
    /// The same stored row says the same thing in two languages.
    /// </summary>
    [Fact]
    public void AHeldQueueExplainsItselfInEitherLanguage()
    {
        MessageKey hold = MessageKey.For("Queue_HoldInsufficientSpace", "bracket.gcode", 4210688, 1048576);

        InCulture("en-GB", () => TestLocaliser.Errors().For(hold))
            .Should().Be("Not enough space on the printer: bracket.gcode needs 4210688 bytes, and 1048576 are free.");

        InCulture("da", () => TestLocaliser.Errors().For(hold))
            .Should().Be("Ikke plads nok på printeren: bracket.gcode kræver 4210688 byte, og der er 1048576 ledige.");
    }

    /// <summary>
    /// Every hold a printer can be in has words behind it, in both languages.
    /// </summary>
    /// <remarks>
    /// Driven from the enum rather than a list, so adding a member without adding words fails here
    /// rather than on somebody's printer page. <see cref="PrintHoldReason.Undefined"/> is excluded
    /// deliberately: it is not a hold, and the read path answers null for it.
    /// </remarks>
    [Fact]
    public void EveryHoldReasonHasWords()
    {
        foreach (PrintHoldReason reason in Enum.GetValues<PrintHoldReason>().Where(r => r != PrintHoldReason.Undefined))
        {
            string key = reason switch
            {
                PrintHoldReason.InsufficientSpace => "Queue_HoldInsufficientSpace",
                PrintHoldReason.FileExistsDifferentSize => "Queue_HoldFileExists",
                PrintHoldReason.FileExistsUnknownSize => "Queue_HoldFileExistsUnknownSize",
                _ => throw new InvalidOperationException($"{reason} has no key; add one to PrintHistoryService too."),
            };

            foreach (string culture in new[] { "en-GB", "da" })
            {
                InCulture(culture, () => TestLocaliser.Shared()[key])
                    .ResourceNotFound.Should().BeFalse($"{reason} is a hold somebody will read in {culture}");
            }
        }
    }

    /// <summary>
    /// Every wait a queue can report has words, or is deliberately silent.
    /// </summary>
    [Fact]
    public void EveryQueueWaitReasonHasWordsOrIsDeliberatelySilent()
    {
        foreach (QueueWaitReason reason in Enum.GetValues<QueueWaitReason>())
        {
            MessageKey? key = QueueWaitDescription.For(QueueAction.Wait(reason), "benchy.bgcode");

            if (key is null)
            {
                continue;
            }

            foreach (string culture in new[] { "en-GB", "da" })
            {
                InCulture(culture, () => TestLocaliser.Shared()[key.Key])
                    .ResourceNotFound.Should().BeFalse($"{reason} names {key.Key}, which must exist in {culture}");
            }
        }
    }

    /// <summary>
    /// The stored value is the member's name, so reordering the enum cannot reinterpret history.
    /// </summary>
    /// <remarks>
    /// <b>The reason this column is text.</b> An integer discriminator makes the order of an enum
    /// part of the schema, silently: insert a member and every stored row means something else. That
    /// risk is worth taking on a table with millions of rows; this one has a handful, and every
    /// value of it that anybody ever reads is read while working out why a printer stopped.
    /// </remarks>
    [Fact]
    public async Task AHoldIsStoredByNameRatherThanByNumber()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        await SeedHeldRowAsync(context);

        string? stored = await context.Database
                                      .SqlQuery<string?>($"SELECT HoldReason AS Value FROM PrintFilesOnPrinters")
                                      .SingleAsync(TestContext.Current.CancellationToken);

        stored.Should().Be("FileExistsDifferentSize", "an integer here would make the enum's order part of the schema");
    }

    /// <summary>
    /// A hold is recognised by its code, not by the words that describe it.
    /// </summary>
    /// <remarks>
    /// <b>The bug this whole change exists to prevent.</b> The free-space check must lift its own
    /// holds and no one else's, and it used to tell them apart by matching the opening words of its
    /// own English. Translate that sentence and the match fails: the queue is held by a reason
    /// nothing recognises, so it never resumes. Reading the code back as an enum is what makes the
    /// comparison independent of language.
    /// </remarks>
    [Fact]
    public async Task AHoldIsRecognisedByItsCodeInAnyLanguage()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();

        await SeedHeldRowAsync(context);
        context.ChangeTracker.Clear();

        PrintFileOnPrinter row = await InCulture(
            "da",
            () => context.PrintFilesOnPrinters.SingleAsync(TestContext.Current.CancellationToken));

        row.HoldReason.Should().Be(PrintHoldReason.FileExistsDifferentSize,
                                   "the comparison the advancer makes must not depend on who is reading");
        row.HoldPrinterFileBytes.Should().Be(8192);
    }

    private static async Task SeedHeldRowAsync(HomespoolDbContext context)
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

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = team.Id };
        PrintFile file = new()
        {
            UserId = 1,
            Name = "bracket.gcode",
            Size = 4096,
            UploadedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        context.PrintFiles.Add(file);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrintFilesOnPrinters.Add(new PrintFileOnPrinter
        {
            PrinterId = printer.Id,
            PrintFileId = file.Id,
            HoldReason = PrintHoldReason.FileExistsDifferentSize,
            HoldPrinterFileBytes = 8192,
            BlockedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static T InCulture<T>(string cultureName, Func<T> body)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        try
        {
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
