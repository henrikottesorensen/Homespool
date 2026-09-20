using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

public class PrinterLiveStateMergerTests
{
    private static PrinterLiveState NewState(int printerId = 1)
    {
        return new() { PrinterId = printerId };
    }

    /// <summary>
    /// A Reduced-mode message - only <c>state</c>, the one required field on
    /// <see cref="TelemetryDTO"/> - must still update <see cref="PrinterLiveState.LastSeenAt"/>
    /// and <see cref="PrinterLiveState.Status"/>, but must not blank out a field it didn't carry.
    /// This is the core behaviour <see cref="PrinterLiveState"/>'s own remarks describe: a null on
    /// the DTO means "not sent", not "reported as absent".
    /// </summary>
    [Fact]
    public void MergeUpdatesLastSeenAndStatusButLeavesUnsentFieldsAtTheirLastKnownValue()
    {
        // Arrange
        PrinterLiveState state = NewState();
        state.Status = PrinterStatus.Idle;
        state.NozzleTemperature = 210.5f;
        state.LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        TelemetryDTO slim = new() { Status = "PRINTING" };
        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;

        // Act
        Merge(state, slim, receivedAt);

        // Assert
        state.Status.Should().Be(PrinterStatus.Printing);
        state.LastSeenAt.Should().Be(receivedAt);
        state.NozzleTemperature.Should().Be(210.5f, "a Reduced message must not blank out a Full-mode field it never sent");
    }

    /// <summary>
    /// A print that ends takes its job fields with it, rather than leaving a finished printer
    /// reporting the last percentage it ever saw.
    /// </summary>
    /// <remarks>
    /// The one place absence is meaningful. Firmware guards the whole block with
    /// <c>if (params.has_job)</c>, outside and before the Full-mode check, so it is sent in every mode
    /// whenever a job exists - which makes "none of it arrived" a reliable statement that there is no
    /// job, rather than a reduced message that merely left it out.
    /// </remarks>
    [Fact]
    public void MergeClearsTheJobBlockWhenThePrinterStopsSendingIt()
    {
        // Arrange - a printer mid-print, as the last message left it
        PrinterLiveState state = NewState();
        state.JobId = 73;
        state.Progress = 99;
        state.TimePrinting = 941;
        state.TimeRemaining = 0;
        state.TimeToFilamentChange = 120;
        state.FilamentUsed = 1015687.625f;
        state.NozzleTemperature = 210.5f;

        // Act - the print has ended, so the block is simply absent
        Merge(state, new TelemetryDTO { Status = "FINISHED" }, DateTimeOffset.UtcNow);

        // Assert
        state.JobId.Should().BeNull();
        state.Progress.Should().BeNull("a finished printer reporting 99% is the defect this fixes");
        state.TimePrinting.Should().BeNull();
        state.TimeRemaining.Should().BeNull();
        state.TimeToFilamentChange.Should().BeNull();

        state.FilamentUsed.Should().Be(1015687.625f,
                                       "it is a lifetime odometer outside the has_job guard - stopping rising is correct, unlike progress");
        state.NozzleTemperature.Should().Be(210.5f, "everything outside the job block still carries forward");
    }

    /// <summary>
    /// And the block survives while any part of it is still arriving, so a message carrying one job
    /// field does not blank the others.
    /// </summary>
    [Fact]
    public void MergeKeepsTheJobBlockWhileAnyPartOfItArrives()
    {
        // Arrange
        PrinterLiveState state = NewState();
        state.JobId = 73;
        state.Progress = 40;
        state.TimePrinting = 500;

        // Act - only progress moved
        Merge(state, new TelemetryDTO { Status = "PRINTING", Progress = 41 },
                                     DateTimeOffset.UtcNow);

        // Assert
        state.Progress.Should().Be(41);
        state.JobId.Should().Be(73, "the job is still running; only this message was thin");
        state.TimePrinting.Should().Be(500);
    }

    [Fact]
    public void MergeOverwritesFieldsThatArePresentInTheMessage()
    {
        // Arrange
        PrinterLiveState state = NewState();

        TelemetryDTO full = new()
        {
            Status = "PRINTING",
            JobId = 42,
            Progress = 55,
            NozzleTemperature = 215.2f,
            BedTemperature = 60.0f,
            Speed = 100,
            Flow = 95,
            Material = "PLA",
        };

        // Act
        Merge(state, full, DateTimeOffset.UtcNow);

        // Assert
        state.JobId.Should().Be(42);
        state.Progress.Should().Be(55);
        state.NozzleTemperature.Should().Be(215.2f);
        state.BedTemperature.Should().Be(60.0f);
        state.Speed.Should().Be(100);
        state.Flow.Should().Be(95);
        state.Material.Should().Be("PLA");
    }

    /// <summary>
    /// Unloading clears the material, because firmware's no-filament sentinel is a statement rather
    /// than a silence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug this exists to prevent is silent and long-lived.</b> <c>"---"</c> is what a printer
    /// reports once its filament is out. Mapping it to a bare null would meet
    /// <c>Field&lt;T&gt;</c>'s implicit conversion, which reads null as <em>Absent</em> - the
    /// coalesce idiom - so the merge would skip it and the row would keep naming PLA for as long as
    /// the printer stayed connected. The page would go on offering to unload an empty machine.
    /// </para>
    /// <para>
    /// So the assertion is specifically that it becomes <b>null</b>: not <c>"---"</c>, which would
    /// mean the sentinel had leaked past the edge, and not <c>"PLA"</c>, which would mean it had been
    /// mistaken for silence.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheNoFilamentSentinelClearsTheMaterialRatherThanBeingKeptOrIgnored()
    {
        // Arrange
        PrinterLiveState state = new() { PrinterId = 1 };

        Merge(state, new TelemetryDTO { Status = "IDLE", Material = "PLA" }, DateTimeOffset.UtcNow);
        state.Material.Should().Be("PLA", "the printer had filament in it to begin with");

        // Act
        Merge(state, new TelemetryDTO { Status = "IDLE", Material = "---" }, DateTimeOffset.UtcNow);

        // Assert
        state.Material.Should().BeNull("the printer said the filament is gone, which is not silence");
    }

    /// <summary>
    /// A per-tool slot reporting the no-filament sentinel clears that slot's material too.
    /// </summary>
    /// <remarks>
    /// <b>The third place this sentinel appears, and the last to be handled.</b> The flat field and
    /// the <c>INFO</c> tools were stripped; the per-slot telemetry path was not, so
    /// <c>PrinterLiveSlotState.Material</c> carried <c>"---"</c> verbatim. It went unnoticed only
    /// because no multi-tool printer had ever been enrolled — and it is the data a per-tool control
    /// would read, which would have offered to unload an empty tool.
    /// </remarks>
    [Fact]
    public void AnEmptySlotReportsNoMaterialRatherThanTheSentinel()
    {
        // Arrange
        PrinterLiveState state = new() { PrinterId = 1 };

        // Act
        Merge(state, new TelemetryDTO
        {
            Status = "IDLE",
            Slot = new SlotsTelemetryDTO
            {
                Active = 2,
                Slots = new Dictionary<string, JsonElement>
                {
                    ["1"] = JsonSerializer.SerializeToElement(new { material = "PLA", temp = 215.0f }),
                    ["2"] = JsonSerializer.SerializeToElement(new { material = "---", temp = 27.0f }),
                },
            },
        }, DateTimeOffset.UtcNow);

        // Assert
        state.Slots.Single(slot => slot.SlotNumber == 1).Material.Should().Be("PLA");
        state.Slots.Single(slot => slot.SlotNumber == 2).Material
             .Should().BeNull("an empty tool is empty, not loaded with a filament called ---");
    }

    /// <summary>
    /// A message that says nothing about the material keeps the last-known one - the other half of
    /// the three-way distinction above, and what stops every partial message clearing the column.
    /// </summary>
    [Fact]
    public void AMessageThatOmitsTheMaterialKeepsTheLastKnownOne()
    {
        // Arrange
        PrinterLiveState state = new() { PrinterId = 1 };

        Merge(state, new TelemetryDTO { Status = "IDLE", Material = "PETG" }, DateTimeOffset.UtcNow);

        // Act
        Merge(state, new TelemetryDTO { Status = "PRINTING", Progress = 10 }, DateTimeOffset.UtcNow);

        // Assert
        state.Material.Should().Be("PETG", "the message was silent about the material, not empty of it");
    }

    /// <summary>
    /// Firmware renders <c>chamber</c> as one atomic block - present or absent as a whole - so a
    /// present block overwrites every field in it, unlike the flat fields above which coalesce
    /// individually.
    /// </summary>
    [Fact]
    public void MergeReplacesTheWholeChamberBlockWhenPresent()
    {
        // Arrange
        PrinterLiveState state = NewState();
        state.ChamberTemperature = 30f;
        state.ChamberLedIntensity = 10;

        TelemetryDTO withChamber = new()
        {
            Status = "PRINTING",
            Chamber = new ChamberTelemetryDTO
            {
                Temperature = 45.5f,
                TargetTemperature = 50,
                Fan1Speed = 3000,
                Fan2Speed = 3100,
                FanPwmTarget = 80,
                LedIntensity = 100,
            },
        };

        // Act
        Merge(state, withChamber, DateTimeOffset.UtcNow);

        // Assert
        state.ChamberTemperature.Should().Be(45.5f);
        state.ChamberTargetTemperature.Should().Be(50);
        state.ChamberFan1Rpm.Should().Be(3000);
        state.ChamberFan2Rpm.Should().Be(3100);
        state.ChamberFanPwmTarget.Should().Be(80);
        state.ChamberLedIntensity.Should().Be(100);
    }

    [Fact]
    public void MergeLeavesChamberFieldsAtLastKnownValueWhenBlockIsAbsent()
    {
        // Arrange
        PrinterLiveState state = NewState();
        state.ChamberTemperature = 30f;
        state.ChamberLedIntensity = 10;

        TelemetryDTO withoutChamber = new() { Status = "PRINTING" };

        // Act
        Merge(state, withoutChamber, DateTimeOffset.UtcNow);

        // Assert
        state.ChamberTemperature.Should().Be(30f);
        state.ChamberLedIntensity.Should().Be(10);
    }

    /// <summary>
    /// Regression test for the <c>EnclosureTelemetryDTO.Temperature</c> fix: firmware renders the
    /// whole enclosure block with <c>JSON_FIELD_INT</c> (<c>render.cpp</c>), unlike chamber's
    /// fixed-point temperature, so this must assign directly with no cast.
    /// </summary>
    [Fact]
    public void MergeCopiesEnclosureFieldsAsIntegers()
    {
        // Arrange
        PrinterLiveState state = NewState();

        TelemetryDTO withEnclosure = new()
        {
            Status = "PRINTING",
            Enclosure = new EnclosureTelemetryDTO { Temperature = 42, FanSpeed = 1200, TimeIsUse = 500 },
        };

        // Act
        Merge(state, withEnclosure, DateTimeOffset.UtcNow);

        // Assert
        state.EnclosureTemperature.Should().Be(42);
        state.EnclosureFanRpm.Should().Be(1200);
        state.EnclosureTimeInUse.Should().Be(500);
    }

    /// <summary>
    /// <c>MmuState</c>/<c>MmuCommand</c> are MMU-only and stay coalesced field-by-field even
    /// though they live inside the otherwise-atomic <c>slot</c> block - an XL sends <c>slot</c>
    /// (tool-changer, more than one tool) without ever populating either.
    /// </summary>
    [Fact]
    public void MergeSlotBlockCoalescesMmuFieldsIndependentlyOfActiveSlot()
    {
        // Arrange
        PrinterLiveState state = NewState();
        state.MmuState = 5;
        state.MmuCommand = "L";

        TelemetryDTO xlLikeSlotBlock = new()
        {
            Status = "PRINTING",
            Slot = new SlotsTelemetryDTO { Active = 2, MmuState = null, MmuCommand = null },
        };

        // Act
        Merge(state, xlLikeSlotBlock, DateTimeOffset.UtcNow);

        // Assert
        state.ActiveSlot.Should().Be(2);
        state.MmuState.Should().Be(5, "a non-MMU printer's slot block never carries this field and must not blank it");
        state.MmuCommand.Should().Be("L");
    }

    [Fact]
    public void MergeUpdatesOnlyTheSlotNumbersPresentInTheMessage()
    {
        // Arrange
        PrinterLiveState state = NewState();

        state.Slots.Add(new PrinterLiveSlotState
        {
            PrinterId = state.PrinterId, SlotNumber = 1, Material = "PLA", Temperature = 210f,
        });

        state.Slots.Add(new PrinterLiveSlotState
        {
            PrinterId = state.PrinterId, SlotNumber = 2, Material = "PETG", Temperature = 230f,
        });

        TelemetryDTO onlySlot1Reported = new()
        {
            Status = "PRINTING",
            Slot = new SlotsTelemetryDTO
            {
                Active = 1,
                Slots = new Dictionary<string, JsonElement>
                {
                    ["1"] = JsonSerializer.SerializeToElement(new ToolTelemetryDTO
                    {
                        Material = "ASA", Temperature = 250f, HotendFan = 8000f, PrintFan = 6000f,
                    }),
                },
            },
        };

        // Act
        Merge(state, onlySlot1Reported, DateTimeOffset.UtcNow);

        // Assert
        PrinterLiveSlotState slot1 = state.Slots.Should().ContainSingle(s => s.SlotNumber == 1).Subject;
        slot1.Material.Should().Be("ASA");
        slot1.Temperature.Should().Be(250f);
        slot1.HotendFanRpm.Should().Be(8000f);
        slot1.PrintFanRpm.Should().Be(6000f);

        PrinterLiveSlotState slot2 = state.Slots.Should().ContainSingle(s => s.SlotNumber == 2).Subject;
        slot2.Material.Should().Be("PETG", "slot 2 was not in this message and must keep its last-known value");
        slot2.Temperature.Should().Be(230f);
    }

    [Fact]
    public void MergeAddsAPreviouslyUnseenSlot()
    {
        // Arrange
        PrinterLiveState state = NewState();

        TelemetryDTO newSlot = new()
        {
            Status = "PRINTING",
            Slot = new SlotsTelemetryDTO
            {
                Active = 3,
                Slots = new Dictionary<string, JsonElement>
                {
                    ["3"] = JsonSerializer.SerializeToElement(new ToolTelemetryDTO
                    {
                        Material = "PC", Temperature = 280f, HotendFan = 9000f, PrintFan = 5000f,
                    }),
                },
            },
        };

        // Act
        Merge(state, newSlot, DateTimeOffset.UtcNow);

        // Assert
        state.Slots.Should().ContainSingle(s => s.SlotNumber == 3 && s.Material == "PC");
    }

    /// <summary>
    /// The 9-value wire vocabulary (<c>const.py</c> <c>State</c>) is exhaustive; anything else -
    /// including the server-synthesised <c>UNKNOWN</c>/<c>MANIPULATING</c>/<c>OFFLINE</c> that
    /// Buddy never actually sends - must fail loudly rather than silently coerce.
    /// </summary>
    [Fact]
    public void MergeThrowsForAStatusOutsideTheNineValueWireVocabulary()
    {
        // Arrange
        PrinterLiveState state = NewState();
        TelemetryDTO badStatus = new() { Status = "UNKNOWN" };

        // Act
        Action act = () => Merge(state, badStatus, DateTimeOffset.UtcNow);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToSampleProjectsTheMergedLiveStateIntoADenseRow()
    {
        // Arrange
        PrinterLiveState state = NewState(printerId: 7);
        state.Status = PrinterStatus.Printing;
        state.JobId = 9;
        state.NozzleTemperature = 215f;
        state.ChamberLedIntensity = 100;
        state.EnclosureTemperature = 42;

        state.Slots.Add(new PrinterLiveSlotState
        {
            PrinterId = 7, SlotNumber = 1, Material = "PLA", Temperature = 210f,
            HotendFanRpm = 8000f, PrintFanRpm = 6000f,
        });

        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        // Act
        TelemetrySample sample = PrinterLiveStateMerger.ToSample(state, timestamp);

        // Assert
        sample.PrinterId.Should().Be(7);
        sample.Timestamp.Should().Be(timestamp);
        sample.Status.Should().Be(PrinterStatus.Printing);
        sample.JobId.Should().Be(9);
        sample.NozzleTemperature.Should().Be(215f);
        sample.ChamberLedIntensity.Should().Be(100);
        sample.EnclosureTemperature.Should().Be(42);

        TelemetrySlotSample slotSample = sample.Slots.Should().ContainSingle().Subject;
        slotSample.SlotNumber.Should().Be(1);
        slotSample.Material.Should().Be("PLA");
        slotSample.Temperature.Should().Be(210f);
        slotSample.HotendFanRpm.Should().Be(8000f);
        slotSample.PrintFanRpm.Should().Be(6000f);
    }

    /// <summary>
    /// The composition this suite historically asserted as one call: the Prusa edge's mapping into
    /// the neutral currency, then the mechanical apply. Kept composed so every policy assertion
    /// here (the job-block clear, the atomic blocks, the coalesce) still bites end to end - a
    /// mutation in either half fails these tests exactly as it did when both halves were one class.
    /// </summary>
    /// <summary>
    /// A float that is infinite or NaN is stored as no reading - not kept, and not left showing the
    /// last good number, because the printer did speak for the field.
    /// </summary>
    /// <remarks>
    /// Nothing has to be malformed for one to arrive: <c>"temp_nozzle":1e39</c> is valid JSON, and a
    /// number too large for a float parses to infinity. SQLite refuses NaN outright, which would
    /// fail a write shared with other printers' samples, and keeps infinity, which then fails every
    /// response that serializes the row.
    /// </remarks>
    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void AFloatThatIsNotFiniteIsStoredAsNoReading(float notFinite)
    {
        // Arrange
        PrinterLiveState state = NewState();

        Merge(state,
              new TelemetryDTO { Status = "PRINTING", NozzleTemperature = 215.2f, BedTemperature = 60.0f, ZAxis = 1.2f },
              DateTimeOffset.UtcNow);

        // Act
        Merge(state,
              new TelemetryDTO { Status = "PRINTING", NozzleTemperature = notFinite, ZAxis = notFinite },
              DateTimeOffset.UtcNow);

        // Assert
        state.NozzleTemperature.Should().BeNull();
        state.ZAxis.Should().BeNull();
        state.BedTemperature.Should().Be(60.0f, "a field the message did not speak for keeps its last-known value");

        TelemetrySample sample = PrinterLiveStateMerger.ToSample(state, DateTimeOffset.UtcNow);

        sample.NozzleTemperature.Should().BeNull();
    }

    /// <summary>
    /// Every float an update can carry goes through the same rule, the per-slot ones included. The
    /// update is filled by reflection, so a float field added to it later and merged without the rule
    /// fails here rather than in production.
    /// </summary>
    [Fact]
    public void NoFloatAnUpdateCarriesCanReachTheStateAsInfinity()
    {
        // Arrange
        PrinterLiveState state = NewState();

        TelemetryUpdate update = new()
        {
            Status = PrinterStatus.Printing,
            Slots = [new SlotUpdate(1, "PLA", float.PositiveInfinity, float.NaN, float.NegativeInfinity)],
        };

        List<System.Reflection.PropertyInfo> floatFields = typeof(TelemetryUpdate)
                                                           .GetProperties()
                                                           .Where(property => property.PropertyType == typeof(Field<float?>))
                                                           .ToList();

        foreach (System.Reflection.PropertyInfo field in floatFields)
        {
            field.SetValue(update, Field<float?>.Of(float.PositiveInfinity));
        }

        // Act
        PrinterLiveStateMerger.Apply(state, update, DateTimeOffset.UtcNow);

        // Assert
        floatFields.Should().HaveCount(FloatsOf(state).Count, "every float on the state has a field that feeds it");

        FloatsOf(state).Should().AllSatisfy(value => value.Should().BeNull());
        FloatsOf(state.Slots.Single()).Should().NotBeEmpty().And.AllSatisfy(value => value.Should().BeNull());
    }

    /// <summary>
    /// The whole path for telemetry a printer wrote with bare <c>nan</c> and <c>inf</c> in it: mended
    /// to quoted literals, read by the wire DTOs as the floats they name, and stored as no reading -
    /// with the connection kept, which the non-nullable chamber and slot floats used to cost.
    /// </summary>
    /// <remarks>
    /// Through the production dispatcher and mapping rather than a deserialize of its own, because
    /// accepting the quoted form is a property of how those two read a message: a read that loses it
    /// throws here.
    /// </remarks>
    [Fact]
    public void TelemetryWrittenWithBareNanAndInfIsReadAndStoredAsNoReading()
    {
        // Arrange
        PrinterLiveState state = NewState();

        Merge(state, new TelemetryDTO { Status = "PRINTING", NozzleTemperature = 215.2f, BedTemperature = 60.0f }, DateTimeOffset.UtcNow);

        byte[] wire = System.Text.Encoding.UTF8.GetBytes(
            """{"state":"PRINTING","temp_nozzle":nan,"temp_bed":60.5,"chamber":{"temp":-inf},"slot":{"active":1,"1":{"material":"PLA","temp":inf,"fan_hotend":nan,"fan_print":5000.0}}}""");

        // Act
        NonFinitePatch? patch = NonFiniteNumberPatcher.TryPatch(wire, default);

        patch.Should().NotBeNull();

        using JsonDocument document = JsonDocument.Parse(patch.Document);

        MessageDispatcher dispatcher = new(NullLogger<MessageDispatcher>.Instance,
                                           new UnknownFieldTracker(NullLogger<UnknownFieldTracker>.Instance),
                                           TimeProvider.System,
                                           PrinterTrafficLogTests.Off);

        TelemetryDTO telemetry = dispatcher.Classify(1, document.RootElement, patch.Tokens)
                                           .Should().BeOfType<InboundTelemetryMessage>().Subject.Telemetry;

        Merge(state, telemetry, DateTimeOffset.UtcNow);

        // Assert
        telemetry.NozzleTemperature.Should().Be(float.NaN, "the DTO carries what the printer said; judging it is not its job");

        state.NozzleTemperature.Should().BeNull();
        state.BedTemperature.Should().Be(60.5f);
        state.ChamberTemperature.Should().BeNull();

        PrinterLiveSlotState slot = state.Slots.Single();

        slot.Temperature.Should().BeNull();
        slot.HotendFanRpm.Should().BeNull();
        slot.PrintFanRpm.Should().Be(5000.0f);
    }

    private static List<float?> FloatsOf(object entity)
    {
        return entity.GetType().GetProperties()
                     .Where(property => property.PropertyType == typeof(float?))
                     .Select(property => (float?)property.GetValue(entity))
                     .ToList();
    }

    private static void Merge(PrinterLiveState state, TelemetryDTO telemetry, DateTimeOffset receivedAt)
    {
        PrinterLiveStateMerger.Apply(state, PrusaTelemetryMapping.ToUpdate(telemetry), receivedAt);
    }
}
