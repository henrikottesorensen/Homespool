using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Host.Telemetry;

namespace Homespool.Host.Test;

/// <summary>
/// What the <c>"slot"</c> object does with keys it was not expecting.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard is a firmware release, not an attacker.</b> <c>SlotsTelemetryDTO.Slots</c> is
/// <c>[JsonExtensionData]</c>, so it collects every key the DTO does not name - not only the
/// numbered slots. A field added beside <c>active</c>/<c>state</c>/<c>command</c> in some later
/// firmware therefore arrives as an extension entry, and parsing its key as a slot number used to
/// throw: one added field would have stopped that printer's telemetry entirely.
/// </para>
/// <para>
/// These drive the real public entry point with real JSON rather than hand-built DTOs, because the
/// property under test is what the deserialiser puts in that dictionary.
/// </para>
/// </remarks>
public sealed class SlotTelemetryToleranceTests
{
    [Fact]
    public void AKeyThatIsNotASlotNumberIsSkippedRatherThanThrown()
    {
        TelemetryDTO telemetry = Parse(
            """{"state":"IDLE","slot":{"active":1,"1":{"material":"PLA"},"progress":{"percent":40}}}""");

        TelemetryUpdate update = PrusaTelemetryMapping.ToUpdate(telemetry);

        update.Slots.Should().ContainSingle("the numbered entry is a slot and the named one is not");
        update.Slots[0].SlotNumber.Should().Be(1);
    }

    [Fact]
    public void ASlotWhoseValueIsNotAnObjectIsSkipped()
    {
        // Same dictionary, and the value is as free-form as the key.
        TelemetryDTO telemetry = Parse("""{"state":"IDLE","slot":{"active":1,"1":{"material":"PLA"},"2":5}}""");

        TelemetryUpdate update = PrusaTelemetryMapping.ToUpdate(telemetry);

        update.Slots.Should().ContainSingle();
        update.Slots[0].SlotNumber.Should().Be(1);
    }

    [Fact]
    public void TheSlotsThatAreSlotsStillArrive()
    {
        // The half that proves the skipping above is discrimination rather than silence.
        TelemetryDTO telemetry = Parse(
            """{"state":"IDLE","slot":{"active":1,"1":{"material":"PLA"},"2":{"material":"PETG"}}}""");

        TelemetryUpdate update = PrusaTelemetryMapping.ToUpdate(telemetry);

        update.Slots.Select(slot => slot.SlotNumber).Should().Equal(1, 2);
    }

    private static TelemetryDTO Parse(string json)
    {
        return JsonSerializer.Deserialize<TelemetryDTO>(json)!;
    }
}
