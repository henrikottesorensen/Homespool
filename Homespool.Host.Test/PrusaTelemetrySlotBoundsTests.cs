using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Host.Telemetry;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// How many slots a printer may state, and how long the strings beside them may be.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard here is an attacker, where <see cref="SlotTelemetryToleranceTests"/>' is a firmware
/// release.</b> A printer is authenticated but not trusted, and every slot number it states becomes
/// a row the merger keeps for good and every later sample copies. A number the real printer never
/// reports is never overwritten either, so what one message lets through outlasts the connection
/// that sent it.
/// </para>
/// <para>
/// Real JSON through the public entry points, for that class's reason: the property under test is
/// what the deserialiser hands the mapping.
/// </para>
/// </remarks>
public sealed class PrusaTelemetrySlotBoundsTests
{
    // ---- slot numbers ----
    [Fact]
    public void ASlotIsTakenAtTheCeilingAndSkippedOnePastIt()
    {
        int ceiling = PrusaConnectConstants.MaxSlotNumber;

        TelemetryUpdate update = PrusaTelemetryMapping.ToUpdate(Telemetry(
            $$"""{"state":"IDLE","slot":{"active":1,"{{ceiling}}":{"material":"PLA"},"{{ceiling + 1}}":{"material":"PA"} } }"""));

        update.Slots.Select(slot => slot.SlotNumber).Should().Equal(ceiling);
    }

    [Fact]
    public void ASlotIsTakenAtOneAndSkippedBelowIt()
    {
        // The wire counts from one, so zero is as much not-a-slot as minus one is.
        TelemetryUpdate update = PrusaTelemetryMapping.ToUpdate(Telemetry(
            """{"state":"IDLE","slot":{"active":1,"-1":{"material":"PA"},"0":{"material":"PC"},"1":{"material":"PLA"}}}"""));

        update.Slots.Select(slot => slot.SlotNumber).Should().Equal(1);
    }

    [Fact]
    public void ASlotNumberSpelledTwiceIsTakenOnce()
    {
        // All three parse to 1. A range check alone would pass every one of them, and a message has
        // room for a hundred thousand spellings.
        TelemetryUpdate update = PrusaTelemetryMapping.ToUpdate(Telemetry(
            """{"state":"IDLE","slot":{"active":1,"1":{"material":"PLA"},"01":{"material":"PETG"},"+1":{"material":"ASA"}}}"""));

        update.Slots.Should().ContainSingle().Which.Material.Should().Be("PLA", "the first spelling wins");
    }

    [Fact]
    public void AnOutOfRangeSlotIsDecidedBeforeItIsRead()
    {
        // A numbered object that will not deserialize throws - deliberately, as protocol drift. One
        // outside the range is not a slot at all, so what is in it must not matter.
        TelemetryDTO telemetry = Telemetry(
            """{"state":"IDLE","slot":{"active":1,"1":{"material":"PLA"},"9":{"temp":"not a number"}}}""");

        Func<TelemetryUpdate> mapping = () => PrusaTelemetryMapping.ToUpdate(telemetry);

        mapping.Should().NotThrow().Which.Slots.Should().ContainSingle();
    }

    [Fact]
    public void TheUnloadCommandAndTheMappingShareOneCeiling()
    {
        // Two numbers that merely agree report nothing when they stop agreeing: a slot the mapping
        // lets through and the command cannot address is a row with a button that throws.
        UnloadFilament.MaxTools.Should().Be(PrusaConnectConstants.MaxSlotNumber);
    }

    [Fact]
    public void AMessageOfTwentyThousandSlotsLeavesEightInTheStateAndInTheSample()
    {
        StringBuilder json = new("""{"state":"IDLE","slot":{"active":1""");

        for (int number = 1; number <= 20_000; number++)
        {
            json.Append(System.Globalization.CultureInfo.InvariantCulture, $$""","{{number}}":{"material":"PLA"}""");
        }

        json.Append("}}");

        PrinterLiveState state = new() { PrinterId = 1 };
        DateTimeOffset receivedAt = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        PrinterLiveStateMerger.Apply(state, PrusaTelemetryMapping.ToUpdate(Telemetry(json.ToString())), receivedAt);

        state.Slots.Should().HaveCount(PrusaConnectConstants.MaxSlotNumber);
        PrinterLiveStateMerger.ToSample(state, receivedAt).Slots.Should().HaveCount(PrusaConnectConstants.MaxSlotNumber);
    }

    // ---- INFO tools ----
    [Fact]
    public void AnInfoToolIsHeldToTheSameRangeAndTakenOnce()
    {
        InfoEventDataDTO info = new()
        {
            Tools = new Dictionary<string, InfoToolDTO>
            {
                ["0"] = new() { Material = "PC" },
                ["1"] = new() { Material = "PLA" },
                ["01"] = new() { Material = "PETG" },
                ["8"] = new() { Material = "PA" },
                ["9"] = new() { Material = "ASA" },
            },
        };

        PrinterIdentityUpdate identity = PrusaTelemetryMapping.ToIdentity(info);

        identity.Tools.Should().NotBeNull();
        identity.Tools!.Select(tool => (tool.ToolNumber, tool.Material)).Should().Equal((1, "PLA"), (8, "PA"));
    }

    // ---- strings ----
    [Fact]
    public void TheFlatMaterialIsTakenAtItsLimitAndReadsAsUnmentionedPastIt()
    {
        Material(PrusaConnectConstants.MaterialMaxLength).IsPresent.Should().BeTrue("a value at the limit is within it");

        // Absent rather than null: null is "the filament is gone", which an over-long name never said.
        Material(PrusaConnectConstants.MaterialMaxLength + 1).IsPresent.Should().BeFalse("last-known stands");

        static Field<string?> Material(int length)
        {
            return PrusaTelemetryMapping.ToUpdate(Telemetry($$"""{"state":"IDLE","material":"{{new string('P', length)}}"}"""))
                                        .Material;
        }
    }

    [Fact]
    public void ASlotsMaterialIsTakenAtItsLimitAndCostsOnlyItselfPastIt()
    {
        Slot(PrusaConnectConstants.MaterialMaxLength).Material.Should().NotBeNull("a value at the limit is within it");

        SlotUpdate over = Slot(PrusaConnectConstants.MaterialMaxLength + 1);

        over.Material.Should().BeNull("one character past it is not");
        over.Temperature.Should().Be(215, "the rest of the slot is still what the printer said");

        static SlotUpdate Slot(int length)
        {
            return PrusaTelemetryMapping.ToUpdate(Telemetry(
                $$"""{"state":"IDLE","slot":{"active":1,"1":{"material":"{{new string('P', length)}}","temp":215} } }""")).Slots[0];
        }
    }

    [Fact]
    public void AnInfoToolsMaterialIsTakenAtItsLimitAndRefusedOneCharacterPastIt()
    {
        Tool(PrusaConnectConstants.MaterialMaxLength).Should().NotBeNull("a value at the limit is within it");
        Tool(PrusaConnectConstants.MaterialMaxLength + 1).Should().BeNull("one character past it is not");

        static string? Tool(int length)
        {
            InfoEventDataDTO info = new()
            {
                Tools = new Dictionary<string, InfoToolDTO> { ["1"] = new() { Material = new string('P', length) } },
            };

            return PrusaTelemetryMapping.ToIdentity(info).Tools![0].Material;
        }
    }

    [Fact]
    public void TheMmuCommandIsTakenAtItsLimitAndReadsAsUnmentionedPastIt()
    {
        Command(PrusaConnectConstants.MmuCommandMaxLength).IsPresent.Should().BeTrue("a value at the limit is within it");
        Command(PrusaConnectConstants.MmuCommandMaxLength + 1).IsPresent.Should().BeFalse("one character past it is not");

        static Field<string?> Command(int length)
        {
            return PrusaTelemetryMapping.ToUpdate(Telemetry(
                $$"""{"state":"IDLE","slot":{"active":1,"command":"{{new string('T', length)}}"} }""")).MmuCommand;
        }
    }

    /// <summary>
    /// Each sensor in its own test, because the two share one limit - a theory keyed on the limit
    /// would pass with either one of them unbounded.
    /// </summary>
    [Fact]
    public void TheExtruderSensorStateIsTakenAtItsLimitAndReadsAsUnmentionedPastIt()
    {
        State(PrusaConnectConstants.FilamentSensorStatusMaxLength).IsPresent.Should().BeTrue();
        State(PrusaConnectConstants.FilamentSensorStatusMaxLength + 1).IsPresent.Should().BeFalse();

        static Field<string?> State(int length)
        {
            return PrusaTelemetryMapping.ToUpdate(Telemetry(
                $$"""{"state":"IDLE","extruder_fs_state":"{{new string('S', length)}}"}""")).ExtruderFilamentSensorStatus;
        }
    }

    [Fact]
    public void TheRemoteSensorStateIsTakenAtItsLimitAndReadsAsUnmentionedPastIt()
    {
        State(PrusaConnectConstants.FilamentSensorStatusMaxLength).IsPresent.Should().BeTrue();
        State(PrusaConnectConstants.FilamentSensorStatusMaxLength + 1).IsPresent.Should().BeFalse();

        static Field<string?> State(int length)
        {
            return PrusaTelemetryMapping.ToUpdate(Telemetry(
                $$"""{"state":"IDLE","remote_fs_state":"{{new string('S', length)}}"}""")).RemoteFilamentSensorStatus;
        }
    }

    [Fact]
    public void AnEventsReasonIsTakenAtItsLimitAndRefusedOneCharacterPastIt()
    {
        Reason(PrusaConnectConstants.ReasonMaxLength).Should().NotBeNull("a value at the limit is within it");
        Reason(PrusaConnectConstants.ReasonMaxLength + 1).Should().BeNull("one character past it is not");

        static string? Reason(int length)
        {
            return Record($$"""{"event":"REJECTED","state":"IDLE","reason":"{{new string('R', length)}}"}""").Reason;
        }
    }

    /// <summary>
    /// <c>text</c> and <c>title</c> apart, for the sensors' reason - and the title only ever shows
    /// through when there is no text, so it has to be tested with none.
    /// </summary>
    [Fact]
    public void AnAttentionsTextIsTakenAtItsLimitAndCostsOnlyItselfPastIt()
    {
        Attention(PrusaConnectConstants.AttentionTextMaxLength)!.Text.Should().NotBeNull();

        PrinterAttentionUpdate? over = Attention(PrusaConnectConstants.AttentionTextMaxLength + 1);

        over.Should().NotBeNull("the code is still a reason the printer gave");
        over!.Text.Should().BeNull();
        over.Code.Should().Be(17108);

        static PrinterAttentionUpdate? Attention(int length)
        {
            return Record(
                $$"""{"event":"STATE_CHANGED","state":"ATTENTION","data":{"code":"17108","text":"{{new string('t', length)}}"} }""")
                .Attention;
        }
    }

    [Fact]
    public void AnAttentionsTitleIsTakenAtItsLimitAndRefusedOneCharacterPastIt()
    {
        Title(PrusaConnectConstants.AttentionTextMaxLength).Should().NotBeNull();
        Title(PrusaConnectConstants.AttentionTextMaxLength + 1).Should().BeNull();

        static string? Title(int length)
        {
            return Record(
                $$"""{"event":"STATE_CHANGED","state":"ATTENTION","data":{"code":"17108","title":"{{new string('T', length)}}"} }""")
                .Attention!.Text;
        }
    }

    // ---- the drive listing ----
    [Fact]
    public void ADriveListingIsKeptAtItsLimitAndKeepsOnlyItsCountPastIt()
    {
        Listing(PrusaConnectConstants.DriveListingMaxBytes).Entries.Should().NotBeNull("a listing at the limit is within it");

        PrinterDriveListingUpdate over = Listing(PrusaConnectConstants.DriveListingMaxBytes + 1);

        over.Entries.Should().BeNull("one byte past it is not");
        over.FileCount.Should().Be(3, "that a listing arrived, and how big it claimed to be, is still recorded");

        static PrinterDriveListingUpdate Listing(int bytes)
        {
            // ["aaa…"] - the brackets and the quotes are four of the bytes.
            string children = "[\"" + new string('a', bytes - 4) + "\"]";

            return Record(
                $$"""{"event":"FILE_INFO","state":"IDLE","data":{"type":"FOLDER","file_count":3,"children":{{children}}} }""")
                .DriveListing!;
        }
    }

    private static TelemetryDTO Telemetry(string json)
    {
        return JsonSerializer.Deserialize<TelemetryDTO>(json)!;
    }

    private static PrinterEventRecord Record(string json)
    {
        return PrusaTelemetryMapping.ToRecord(JsonSerializer.Deserialize<EventDTO>(json)!, identity: null);
    }
}
