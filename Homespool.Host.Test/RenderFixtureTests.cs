using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Telemetry;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// Deserializes output produced by Buddy's own Connect renderer - the printer-&gt;server half of the
/// protocol - across the 18 scenarios its unit tests cover, several of which no capture this project
/// holds has ever contained.
/// </summary>
/// <remarks>
/// <para>
/// <c>render-fixtures.json</c> is <b>generated, not hand-copied</b>. Prusa's
/// <c>tests/unit/connect/render.cpp</c> asserts each scenario against a string literal locked inside
/// a Catch2 <c>SECTION</c> body - not enumerable, and three of them are built at runtime from a live
/// transfer id - so the fixture instead comes from a <c>connect_render_dump</c> target that runs the
/// real <c>Renderer</c> over the same setups and prints what it produces. That makes this file
/// evidence of <i>what their renderer emitted when we ran it</i> rather than of what their test file
/// claims it emits, and regenerating at a newer firmware revision is a command rather than a re-read.
/// </para>
/// <para>
/// Generated from the pinned upstream ref <c>e96ce2b92</c> (v6.6.0), the same ref every firmware
/// citation in this codebase was read at. To regenerate, build <c>connect_render_dump</c> in the
/// firmware checkout and redirect its stdout over this file.
/// Nothing here reads the firmware checkout at test time - it is a machine-local path,
/// and a fixture that silently vanishes on another machine is worse than one that is committed.
/// </para>
/// <para>
/// <b>Provenance, honestly stated:</b> this is a real renderer's real output, but driven by a
/// <c>MockPrinter</c> on a frozen clock rather than by hardware. Where it overlaps
/// <c>websocket.capture</c>, that capture remains the higher authority - it is bytes observed on a
/// wire. Where it does not overlap, this is the best oracle available, and it covers
/// <c>TRANSFER_INFO</c>, <c>STATE_CHANGED</c> with a dialog, and the multi-tool <c>INFO</c> shape,
/// none of which any capture here contains.
/// </para>
/// </remarks>
public class RenderFixtureTests
{
    /// <summary>
    /// Every scenario in the fixture, in the order the dumper emits them, named exactly as the
    /// section it came from. The four "Even -" spellings are Prusa's own typos, kept verbatim
    /// because they are the join key back to their file - correcting them here would quietly break
    /// the correspondence this fixture depends on.
    /// </summary>
    private static readonly string[] ExpectedScenarios =
    [
        "Telemetry - reduced",
        "Telemetry - printing",
        "Telemetry - idle",
        "Telemetry - transferring",
        "Telemetry with background command",
        "Event - rejected",
        "Event - job info",
        "Even - job info not printing",
        "Even - job info - invalid job ID",
        "Even - job info - old job ID FINISHED",
        "Even - job info - old job ID ABORTED",
        "Event - info",
        "Event - info - multi",
        "Event - transfer info, no transfer",
        "Event - transfer info",
        "Event - rejected with transfer",
        "Event - transfer info no upload path",
        "Event - state changed with dialog",
    ];

    /// <summary>
    /// Guards the fixture itself. A regeneration that dropped or reordered scenarios - or a
    /// firmware revision that removed one - would otherwise leave the tests below quietly asserting
    /// over less than they claim to.
    /// </summary>
    [Fact]
    public void TheFixtureContainsEveryRenderScenario()
    {
        // Arrange
        // Act
        IReadOnlyList<RenderFixture> fixtures = LoadFixtures();

        // Assert
        fixtures.Select(fixture => fixture.Scenario).Should().Equal(ExpectedScenarios);
    }

    /// <summary>
    /// The five telemetry scenarios deserialize into <see cref="TelemetryDTO"/>, covering both
    /// <c>SendTelemetry::Mode</c> values plus the two shapes no capture holds: mid-transfer
    /// telemetry, and telemetry carrying a background <c>command_id</c>.
    /// </summary>
    [Fact]
    public void EveryTelemetryScenarioDeserializes()
    {
        // Arrange
        IReadOnlyList<RenderFixture> fixtures = LoadFixtures();

        // Act
        Dictionary<string, TelemetryDTO> telemetry = fixtures
                                                     .Where(fixture => !fixture.Output.TryGetProperty("event", out _))
                                                     .ToDictionary(
                                                         fixture => fixture.Scenario,
                                                         fixture => JsonSerializer.Deserialize<TelemetryDTO>(fixture.Output)!);

        // Assert
        telemetry.Should().HaveCount(5);

        // Reduced mode is the slim shape a printing Buddy sends between full refreshes. Its six
        // fields are the whole message - asserting the absent ones are null is what proves the DTO
        // tells "not sent" apart from "sent as zero", which the merge logic downstream relies on.
        TelemetryDTO reduced = telemetry["Telemetry - reduced"];
        reduced.JobId.Should().Be(42);
        reduced.Progress.Should().Be(12);
        reduced.Status.Should().Be("PRINTING");
        reduced.TimeToFilamentChange.Should().Be(0, "filament_change_in is part of the reduced shape - "
                                                    + "the omission this fixture independently confirmed");
        reduced.NozzleTemperature.Should().BeNull();
        reduced.Chamber.Should().BeNull();

        TelemetryDTO printing = telemetry["Telemetry - printing"];
        printing.NozzleTemperature.Should().Be(200.0f);
        printing.BedTemperature.Should().Be(65.0f);
        printing.TargetNozzleTemperature.Should().Be(195.0f);
        printing.TargetBedTemperature.Should().Be(70.0f);
        printing.Chamber.Should().NotBeNull();
        printing.Chamber!.FanPwmTarget.Should().Be(-1, "an idle chamber fan reports -1, not 0 - a "
                                                       + "signed field that an unsigned DTO would reject");
        printing.ZAxis.Should().Be(0.00f);
        printing.XAxis.Should().BeNull("a printing machine reports no X/Y - they are mutually "
                                       + "exclusive with the job block");

        // The inverse: idle telemetry has X and Y but no job block at all.
        TelemetryDTO idle = telemetry["Telemetry - idle"];
        idle.Status.Should().Be("IDLE");
        idle.XAxis.Should().Be(0.00f);
        idle.YAxis.Should().Be(0.00f);
        idle.JobId.Should().BeNull();
        idle.Progress.Should().BeNull();

        // Mid-transfer telemetry: four flat transfer_* fields at the root, never seen in any
        // capture this project holds.
        TelemetryDTO transferring = telemetry["Telemetry - transferring"];
        transferring.TransferId.Should().NotBeNull();
        transferring.TransferTransferred.Should().Be(0);
        transferring.TransferTimeRemaining.Should().Be(0);
        transferring.TransferProgress.Should().Be(0.0);

        // A background command's id rides on ordinary telemetry rather than on an event.
        telemetry["Telemetry with background command"].CommandId.Should().Be(13u);
    }

    /// <summary>
    /// The thirteen event scenarios deserialize into <see cref="EventDTO"/>, and produce the event
    /// types their sections describe - including the three <c>REJECTED</c> results that Prusa's own
    /// tests show a <c>JOB_INFO</c> request turning into.
    /// </summary>
    [Fact]
    public void EveryEventScenarioDeserializes()
    {
        // Arrange
        IReadOnlyList<RenderFixture> fixtures = LoadFixtures();

        // Act
        Dictionary<string, EventDTO> events = EventsByScenario(fixtures);

        // Assert
        events.Should().HaveCount(13);

        events["Event - rejected"].EventType.Should().Be(PrinterEventType.Rejected);
        events["Event - rejected"].CommandId.Should().Be(11u);
        events["Event - rejected"].Status.Should().Be("IDLE");

        events["Event - job info"].EventType.Should().Be(PrinterEventType.JobInfo);
        events["Event - job info"].JobId.Should().Be(42);

        // A JOB_INFO request that cannot be answered comes back as REJECTED with a reason, not as
        // an empty JOB_INFO - three different ways, all of which a server has to expect.
        events["Even - job info not printing"].EventType.Should().Be(PrinterEventType.Rejected);
        events["Even - job info not printing"].Reason.Should().Be("No job in progress");
        events["Even - job info - invalid job ID"].EventType.Should().Be(PrinterEventType.Rejected);
        events["Even - job info - invalid job ID"].Reason.Should().Be("Job ID doesn't match");

        // A finished job answers with its terminal state instead of its details.
        events["Even - job info - old job ID FINISHED"].Data!.Value.GetProperty("state").GetString()
                                                       .Should().Be("FIN_OK");
        events["Even - job info - old job ID ABORTED"].Data!.Value.GetProperty("state").GetString()
                                                      .Should().Be("FIN_STOPPED");

        events["Event - info"].EventType.Should().Be(PrinterEventType.Info);
        events["Event - transfer info, no transfer"].EventType.Should().Be(PrinterEventType.TransferInfo);
        events["Event - state changed with dialog"].EventType.Should().Be(PrinterEventType.StateChanged);
    }

    /// <summary>
    /// The <c>INFO</c> event's payload deserializes into <see cref="InfoEventDataDTO"/>, in both the
    /// single-tool and multi-tool shapes. The multi-tool one cross-checks what had to be derived
    /// from firmware source, there being no multi-tool printer to capture from.
    /// </summary>
    [Fact]
    public void TheInfoEventPayloadDeserializes()
    {
        // Arrange
        Dictionary<string, EventDTO> events = EventsByScenario(LoadFixtures());

        // Act
        InfoEventDataDTO? single = events["Event - info"].Data!.Value.Deserialize<InfoEventDataDTO>();
        InfoEventDataDTO? multi = events["Event - info - multi"].Data!.Value.Deserialize<InfoEventDataDTO>();

        // Assert
        single.Should().NotBeNull();
        single!.Firmware.Should().Be("TST-1234");
        single.PrinterType.Should().Be("2.3.0");
        single.SerialNumber.Should().Be("FAKE-1234");
        single.Fingerprint.Should().Be("DEADBEEF");
        single.NozzleDiameter.Should().Be(0.40f);
        single.TransferPaused.Should().BeTrue();
        single.Slots.Should().Be(1);
        single.Storages.Should().BeEmpty("an empty storages array is still an array - it is absent "
                                         + "only when the field itself is omitted");
        single.NetworkInfo.Should().NotBeNull();
        single.Tools.Should().ContainKey("1");
        single.Tools!["1"].Material.Should().Be("---", "the no-filament sentinel is a literal string");

        // Slots are 1-based and sparse: this printer has slots 1 and 3 populated and 2 absent, which
        // a list-shaped DTO would have silently renumbered.
        multi.Should().NotBeNull();
        multi!.Slots.Should().Be(2);
        multi.Tools.Should().ContainKeys("1", "3");
        multi.Tools.Should().NotContainKey("2");
        multi.Tools!["3"].NozzleDiameter.Should().Be(0.60f);
        multi.Tools["3"].Hardened.Should().BeTrue();
        multi.Tools["3"].Material.Should().Be("PETG");
    }

    /// <summary>
    /// Pins <c>TRANSFER_INFO</c>'s wire shape from the renderer directly - the contract the transfer
    /// phase is built against. Deliberately asserted on the raw <see cref="EventDTO.Data"/> element
    /// even though <see cref="TransferEventDataDTO"/> now exists: this test is the wire contract, and
    /// a typed assertion would only prove the DTO agrees with itself. <see cref="TheTransferInfoPayloadDeserializes"/>
    /// is the one that checks the mapping.
    /// </summary>
    [Fact]
    public void TheTransferInfoEventCarriesTheTransferShape()
    {
        // Arrange
        Dictionary<string, EventDTO> events = EventsByScenario(LoadFixtures());

        // Act
        EventDTO active = events["Event - transfer info"];
        EventDTO idle = events["Event - transfer info, no transfer"];
        EventDTO noPath = events["Event - transfer info no upload path"];

        // Assert
        JsonElement data = active.Data!.Value;
        data.GetProperty("size").GetInt64().Should().Be(1024);
        data.GetProperty("transferred").GetInt64().Should().Be(0);
        data.GetProperty("progress").GetDouble().Should().Be(0.0);
        data.GetProperty("time_remaining").GetInt64().Should().Be(0);
        data.GetProperty("time_transferring").GetInt64().Should().Be(0);
        data.GetProperty("path").GetString().Should().Be("/usb/whatever.gcode");
        data.GetProperty("type").GetString().Should().Be("FROM_CONNECT",
                                                         "the wire spelling of the transfer type, which nothing in this project had yet seen");

        // With no transfer running the payload collapses to a type alone - so a consumer cannot
        // assume the other fields exist.
        idle.Data!.Value.GetProperty("type").GetString().Should().Be("NO_TRANSFER");
        idle.Data.Value.TryGetProperty("size", out _).Should().BeFalse();

        // An upload with no destination path yet omits the field rather than sending null or "".
        noPath.Data!.Value.TryGetProperty("path", out _).Should().BeFalse();

        // transfer_id is a sibling of event/state at the root, not part of data - and it is a
        // 31-bit unsigned on the wire (firmware's Custom_uint31_t), which is exactly why the DTO
        // can type it as a signed int without risking the top half of the range.
        active.TransferId.Should().NotBeNull();
        active.TransferId.Should().BePositive();

        // It also rides on events that are not about transfers at all, whenever one is running.
        events["Event - rejected with transfer"].TransferId.Should().NotBeNull();
    }

    /// <summary>
    /// <see cref="TransferEventDataDTO"/> maps the payload the renderer actually produced, in both
    /// its shapes - the full progress block, and the bare <c>type</c> of a <c>TRANSFER_INFO</c>
    /// answered with nothing running.
    /// </summary>
    [Fact]
    public void TheTransferInfoPayloadDeserializes()
    {
        // Arrange
        Dictionary<string, EventDTO> events = EventsByScenario(LoadFixtures());

        // Act
        TransferEventDataDTO? active = events["Event - transfer info"].Data!.Value
                                                                      .Deserialize<TransferEventDataDTO>();
        TransferEventDataDTO? none = events["Event - transfer info, no transfer"].Data!.Value
                                                                                 .Deserialize<TransferEventDataDTO>();
        TransferEventDataDTO? noPath = events["Event - transfer info no upload path"].Data!.Value
                                                                                     .Deserialize<TransferEventDataDTO>();

        // Assert
        active.Should().NotBeNull();
        active!.Size.Should().Be(1024);
        active.Transferred.Should().Be(0);
        active.Progress.Should().Be(0.0);
        active.TimeRemaining.Should().Be(0);
        active.TimeTransferring.Should().Be(0);
        active.Path.Should().Be("/usb/whatever.gcode");
        active.Type.Should().Be("FROM_CONNECT");

        // Nothing running: type alone, everything else absent rather than zeroed.
        none.Should().NotBeNull();
        none!.Type.Should().Be("NO_TRANSFER");
        none.Size.Should().BeNull();
        none.Transferred.Should().BeNull();

        noPath!.Path.Should().BeNull("firmware guards the field on a non-null destination rather "
                                     + "than sending an empty one");

        // None of these scenarios sets start_cmd_id - their test never exercises it - so the field
        // being null here is the renderer's behaviour, not a mapping failure. The shape that does
        // carry it is covered by TheTerminalTransferEventCarriesTheStartingCommandId.
        active.StartCommandId.Should().BeNull();
    }

    /// <summary>
    /// The terminal transfer events' payload, which is <b>only</b> <c>start_cmd_id</c> - and which
    /// nests it inside <c>data</c> while <c>command_id</c> and <c>transfer_id</c> on the same events
    /// sit at the root.
    /// </summary>
    /// <remarks>
    /// Hand-built rather than generated, because no <c>render.cpp</c> section produces a terminal
    /// transfer event - reaching one needs a transfer that actually completes, which their renderer
    /// tests never run. The shape is read directly from the emitting branch (render.cpp:538-543 at
    /// the pinned ref): a <c>data</c> object containing that one field, omitted entirely when the
    /// id is absent. Weaker provenance than the rest of this file, and deliberately marked as such.
    /// </remarks>
    [Fact]
    public void TheTerminalTransferEventCarriesTheStartingCommandId()
    {
        // Arrange
        const string finished = """
                                {"data":{"start_cmd_id":11},"transfer_id":1037732555,"state":"IDLE","event":"TRANSFER_FINISHED"}
                                """;

        // Act
        EventDTO? eventDto = JsonSerializer.Deserialize<EventDTO>(finished);
        TransferEventDataDTO? data = eventDto!.Data!.Value.Deserialize<TransferEventDataDTO>();

        // Assert
        eventDto.EventType.Should().Be(PrinterEventType.TransferFinished);
        eventDto.TransferId.Should().Be(1037732555);
        eventDto.CommandId.Should().BeNull("the terminal events are unsolicited - they answer no "
                                           + "command, which is why start_cmd_id has to exist");
        data!.StartCommandId.Should().Be(11u);
    }

    /// <summary>
    /// <c>STATE_CHANGED</c> carrying a dialog - the shape behind <c>ATTENTION</c>, and the one that
    /// tells a waiting-for-a-human printer apart from a merely paused one. Never seen in a capture
    /// here, and like <c>TRANSFER_INFO</c> it has no typed payload DTO yet.
    /// </summary>
    [Fact]
    public void TheStateChangedEventCarriesItsDialog()
    {
        // Arrange
        Dictionary<string, EventDTO> events = EventsByScenario(LoadFixtures());

        // Act
        EventDTO stateChanged = events["Event - state changed with dialog"];

        // Assert
        stateChanged.Status.Should().Be("ATTENTION");

        // dialog_id is a root-level sibling of state, not part of data - the DTO models it there,
        // and this is the first evidence that placement is right.
        stateChanged.DialogId.Should().Be(42u);

        JsonElement data = stateChanged.Data!.Value;
        data.GetProperty("code").GetString().Should().Be("00000",
                                                         "the error code is a zero-padded string, not a number");
        data.GetProperty("buttons").EnumerateArray().Select(button => button.GetString())
            .Should().Equal("Yes", "No");
    }

    private static IReadOnlyList<RenderFixture> LoadFixtures()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText("render-fixtures.json"));

        // Clone each output element: it belongs to the document, which is disposed on the way out
        // of this method, and a JsonElement into a disposed document throws when read.
        return document.RootElement.EnumerateArray()
                       .Select(entry => new RenderFixture(
                                   entry.GetProperty("scenario").GetString()!,
                                   entry.GetProperty("output").Clone()))
                       .ToList();
    }

    /// <summary>Every fixture whose output has an <c>"event"</c> property, deserialized and keyed by
    /// scenario name - the same telemetry-or-event split <c>MessageDispatcher</c> makes.</summary>
    private static Dictionary<string, EventDTO> EventsByScenario(IReadOnlyList<RenderFixture> fixtures)
    {
        return fixtures
               .Where(fixture => fixture.Output.TryGetProperty("event", out _))
               .ToDictionary(
                   fixture => fixture.Scenario,
                   fixture => JsonSerializer.Deserialize<EventDTO>(fixture.Output)!);
    }

    /// <summary>One entry of the generated fixture: the section name it came from, and the exact
    /// document the renderer produced, embedded as JSON rather than as an escaped string so the file
    /// stays readable and diffable.</summary>
    private sealed record RenderFixture(string Scenario, JsonElement Output);
}
