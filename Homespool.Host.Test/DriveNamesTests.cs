using System.Linq;

using AwesomeAssertions;

using Homespool.Host.PrintFiles;
using Homespool.Host.Queue;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="DriveNames"/> - what a file is called on a printer's drive when its own name is taken.
/// </summary>
public sealed class DriveNamesTests
{
    [Fact]
    public void AFreeNameIsTheFilesOwn()
    {
        DriveNames.First("part.gcode", "bob", _ => false).Should().Be("part.gcode");
    }

    [Fact]
    public void ATakenNameGetsTheOwnersNameBeforeTheExtension()
    {
        DriveNames.First("part.gcode", "bob", name => name == "part.gcode").Should().Be("part (bob).gcode");
    }

    [Fact]
    public void WhenTheOwnersNameIsTakenTooANumberIsAdded()
    {
        DriveNames.First("part.gcode", "bob", name => name is "part.gcode" or "part (bob).gcode")
                  .Should().Be("part (bob 2).gcode");
    }

    /// <summary>The drive folds case, so a name differing only in case is the same name there.</summary>
    [Fact]
    public void TheNameAfterARefusedOneIsTheNextEvenInAnotherCase()
    {
        DriveNames.After("PART (BOB).gcode", "part.gcode", "bob", _ => false).Should().Be("part (bob 2).gcode");
    }

    [Fact]
    public void WithoutAnOwnersNameTheSuffixIsANumber()
    {
        DriveNames.First("part.gcode", "  ", name => name == "part.gcode").Should().Be("part (2).gcode");
    }

    [Fact]
    public void WhenEveryNameIsTakenThereIsNone()
    {
        DriveNames.First("part.gcode", "bob", _ => true).Should().BeNull();
    }

    /// <summary>
    /// The stem gives way to the printer's limit, never the owner or the extension - a suffix cut short
    /// would say nothing, and an extension cut short would not print.
    /// </summary>
    [Fact]
    public void ALongNameIsShortenedInItsStemToFitThePrintersLimit()
    {
        string longest = new string('a', UserFileStore.MaxNameLength - ".bgcode".Length) + ".bgcode";

        string? name = DriveNames.First(longest, "bob", candidate => candidate == longest);

        name.Should().HaveLength(UserFileStore.MaxNameLength);
        name.Should().EndWith(" (bob).bgcode");
    }

    /// <summary>A cut through a character outside the basic plane would leave half of it, which is not a name.</summary>
    [Fact]
    public void ShorteningNeverSplitsACharacterInTwo()
    {
        string stem = new string('a', UserFileStore.MaxNameLength - ".gcode".Length - " (bob)".Length - 1) + "\U0001F600";

        string? name = DriveNames.First(stem + ".gcode", "bob", candidate => candidate == stem + ".gcode");

        name.Should().NotBeNull();
        name!.Where(char.IsSurrogate).Count().Should().Be(0, "the half that did not fit takes its partner with it");
        name.Should().EndWith(" (bob).gcode");
    }

    /// <summary>Whatever the account rules become, nothing the printer's FAT refuses reaches the drive.</summary>
    [Fact]
    public void AnOwnersNameIsCleanedOfWhatTheDriveRefuses()
    {
        DriveNames.First("part.gcode", "bo:b/?", name => name == "part.gcode").Should().Be("part (bo-b--).gcode");
    }
}
