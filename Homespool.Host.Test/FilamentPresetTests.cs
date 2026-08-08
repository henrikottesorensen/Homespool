using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// The filament table: the numbers themselves, and the one entry that depends on the printer.
/// </summary>
/// <remarks>
/// The temperatures are asserted rather than assumed because they are transcribed from firmware's
/// own table (<c>src/common/filament_presets.cpp</c>) and a transcription error is invisible - a
/// wrong number here produces a printer that heats to something plausible and prints badly, with
/// nothing anywhere reporting a fault.
/// </remarks>
public class FilamentPresetTests
{
    [Theory]
    [InlineData("PLA", 215, 60)]
    [InlineData("PETG", 230, 85)]
    [InlineData("ABS", 255, 100)]
    [InlineData("PC", 275, 100)]
    [InlineData("PA", 285, 100)]
    [InlineData("FLEX", 240, 50)]
    public void ThePresetsMatchFirmwaresOwnTable(string name, int nozzle, int bed)
    {
        FilamentPreset? preset = FilamentPreset.Find("MK4", name);

        preset.Should().NotBeNull();
        preset!.NozzleTemperature.Should().Be(nozzle);
        preset.BedTemperature.Should().Be(bed);
    }

    /// <summary>
    /// PA is cooler on a MINI, and that is a correctness matter rather than a preference.
    /// </summary>
    /// <remarks>
    /// Firmware has <c>PRINTER_IS_PRUSA_MINI() ? 280 : 285</c> because a MINI's maximum nozzle
    /// temperature is lower. Sending 285 there asks for a target the printer will refuse, so the
    /// difference is mirrored rather than rounded away.
    /// </remarks>
    [Fact]
    public void PaIsCoolerOnAMini()
    {
        FilamentPreset.Find("MINI", "PA")!.NozzleTemperature.Should().Be(280);
        FilamentPreset.Find("MK3.5", "PA")!.NozzleTemperature.Should().Be(285);
    }

    /// <summary>Only PA moves - the model must not quietly shift anything else.</summary>
    [Fact]
    public void NothingElseChangesWithTheModel()
    {
        foreach (FilamentPreset standard in FilamentPreset.For("MK4"))
        {
            FilamentPreset onMini = FilamentPreset.Find("MINI", standard.Name)!;

            if (standard.Name == "PA")
            {
                continue;
            }

            onMini.Should().Be(standard, "only PA has a model-dependent target");
        }
    }

    /// <summary>An unknown name selects nothing, rather than falling back to something hot.</summary>
    [Theory]
    [InlineData("ASA")]
    [InlineData("")]
    [InlineData("' OR 1=1")]
    [InlineData(null)]
    public void AnUnknownFilamentIsNotFound(string? name)
    {
        FilamentPreset.Find("MK4", name).Should().BeNull();
    }

    /// <summary>Case is not the user's problem.</summary>
    [Fact]
    public void TheNameIsMatchedCaseInsensitively()
    {
        FilamentPreset.Find("MK4", "petg").Should().NotBeNull();
    }

    /// <summary>
    /// Every preset is a line the allowlist will actually carry - the two halves must not drift
    /// apart, since a preset the encoder refuses is a button that fails at the last moment.
    /// </summary>
    [Fact]
    public void EveryPresetIsSendable()
    {
        foreach (FilamentPreset preset in FilamentPreset.For("MINI"))
        {
            Homespool.Host.PrusaConnect.Commands.GcodeAllowList
                     .IsAllowed($"M104 S{preset.NozzleTemperature}").Should().BeTrue(preset.Name);
            Homespool.Host.PrusaConnect.Commands.GcodeAllowList
                     .IsAllowed($"M140 S{preset.BedTemperature}").Should().BeTrue(preset.Name);
        }
    }
}
