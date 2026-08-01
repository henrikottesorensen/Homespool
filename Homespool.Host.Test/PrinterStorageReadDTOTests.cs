using System;
using System.Collections.Generic;

using AwesomeAssertions;

using Homespool.Host.DTO;
using Homespool.Host.PrusaConnect.DTO.EventMessages;

namespace Homespool.Host.Test;

/// <summary>
/// The translation from firmware's <c>FILE_INFO</c> vocabulary into the one this API reports -
/// which exists because <c>/api/v1</c> is ours and only <c>/p/*</c> owes Prusa anything.
/// </summary>
/// <remarks>
/// The parse itself is proven against a real Core One in <see cref="CaptureReplayTests"/>; what is
/// under test here is only what we do to it afterwards.
/// </remarks>
public class PrinterStorageReadDTOTests
{
    [Theory]
    [InlineData("PRINT_FILE", "printFile")]
    [InlineData("FOLDER", "folder")]
    [InlineData("FILE", "file")]

    // Not a value anyone has seen. It arrives as a sensible string rather than being dropped or
    // throwing, which is the whole reason this is a mechanical conversion and not a lookup table -
    // firmware's set is firmware's to grow.
    [InlineData("SOME_FUTURE_KIND", "someFutureKind")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void KindIsConvertedFromFirmwaresVocabularyMechanically(string? wireType, string? expected)
    {
        PrinterStorageReadDTO.FromEvent(new FileInfoEventDataDTO { Type = wireType })
                             .Kind.Should().Be(expected);
    }

    [Fact]
    public void ModifiedTimestampBecomesARealInstant()
    {
        // Arrange - the m_timestamp of the file in the captured listing.
        FileInfoEventDataDTO data = new()
        {
            Type = "FOLDER",
            Children = [new FileInfoChildDTO { ModifiedTimestamp = 1766393771 }, new FileInfoChildDTO()],
        };

        // Act
        IReadOnlyList<PrinterStorageEntryDTO> entries = PrinterStorageReadDTO.FromEvent(data).Entries!;

        // Assert
        entries[0].ModifiedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1766393771));
        entries[1].ModifiedAt.Should().BeNull("firmware omits the field rather than sending a zero");
    }

    /// <summary>
    /// The wire's <c>name</c>/<c>display_name</c> pair is the easiest thing here to get backwards,
    /// and getting it backwards would show users <c>BARREL~4.BGC</c> as the name of their file.
    /// </summary>
    [Fact]
    public void AChildsLongNameBecomesNameAndItsAliasBecomesShortName()
    {
        // Arrange - an entry exactly as the captured Core One listing renders one.
        FileInfoEventDataDTO data = new()
        {
            Path = "/usb",
            DisplayName = "usb",
            Type = "FOLDER",
            ReadOnly = false,
            FileCount = 1,
            Children =
            [
                new FileInfoChildDTO
                {
                    Name = "BARREL~4.BGC",
                    DisplayName = "Barrel 2_0.25n_0.07mm_PLA_COREONE_8h47m.bgcode",
                    Size = 17479245,
                    ModifiedTimestamp = 1764616095,
                    ReadOnly = false,
                    Type = "PRINT_FILE",
                },
            ],
        };

        // Act
        PrinterStorageReadDTO listing = PrinterStorageReadDTO.FromEvent(data);

        // Assert
        listing.Path.Should().Be("/usb");
        listing.Kind.Should().Be("folder");

        PrinterStorageEntryDTO entry = listing.Entries.Should().ContainSingle().Subject;

        entry.Name.Should().Be("Barrel 2_0.25n_0.07mm_PLA_COREONE_8h47m.bgcode");
        entry.ShortName.Should().Be("BARREL~4.BGC");
        entry.Size.Should().Be(17479245);
        entry.ModifiedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1764616095));
        entry.Kind.Should().Be("printFile");
    }

    /// <summary>
    /// Null entries and empty entries mean different things, and flattening them would report a file
    /// as an empty directory.
    /// </summary>
    [Fact]
    public void AFileHasNoEntriesWhileAnEmptyDirectoryHasNone()
    {
        // Arrange
        FileInfoEventDataDTO file = new() { Path = "/usb/LAMPEN~1.BGC", Type = "PRINT_FILE" };
        FileInfoEventDataDTO emptyFolder = new() { Path = "/usb", Type = "FOLDER", Children = [], FileCount = 0 };

        // Act & Assert
        PrinterStorageReadDTO.FromEvent(file).Entries.Should().BeNull("a file renders no children at all");
        PrinterStorageReadDTO.FromEvent(emptyFolder).Entries.Should().NotBeNull().And.BeEmpty();
    }
}
