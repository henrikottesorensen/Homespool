using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The default policy against the planner's rules (planner.cpp:660-800, 1075-1112): the JC macro's
/// state-dependent Finished/Rejected, SET_PRINTER_READY's STATE_CHANGED, SEND_INFO's INFO,
/// duplicate-id refusal, and the gcode background-command window.
/// </summary>
public class FirmwareFaithfulPolicyTests
{
    private readonly PrinterIdentity _identity = PrinterIdentity.CreateRandom();
    private readonly FakeDevice _device = new();

    /// <summary>PAUSE_PRINT while printing answers FINISHED under the command id, with the job id.</summary>
    /// <remarks>
    /// The <c>state</c> assertion is the interesting one, and it was <b>backwards until 2026-07-27</b>:
    /// it read <c>PAUSED</c>, justified as "the ack renders the post-transition state". A live capture
    /// says otherwise - a real MK3.5 answers <c>PAUSE_PRINT</c> with <c>state=PRINTING</c>, because
    /// job control is asynchronous and the event is rendered before Marlin has moved. See
    /// <see cref="FirmwareFaithfulPolicy"/>'s <c>JobControl</c> remarks.
    /// </remarks>
    [Fact]
    public void PauseWhilePrintingAnswersFinished()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);
        _device.StartPrint(jobId: 301);

        IReadOnlyList<PlannedReply> replies = policy.Answer(JsonCommand(5, "PAUSE_PRINT"), _device);

        replies.Should().HaveCount(1);
        using JsonDocument reply = Parse(replies[0]);
        reply.RootElement.GetProperty("event").GetString().Should().Be("FINISHED");
        reply.RootElement.GetProperty("command_id").GetUInt32().Should().Be(5);
        reply.RootElement.GetProperty("job_id").GetInt32().Should().Be(301);
        reply.RootElement.GetProperty("state").GetString().Should().Be("PRINTING",
            "the ack reports the state at render time, and the pause has not taken effect yet");
        _device.State.Should().Be(DeviceState.Paused, "the device itself has still transitioned");
    }

    /// <summary>
    /// RESUME_PRINT answers FINISHED reporting <c>PAUSED</c> - the mirror of the pause case, and the
    /// second half of the same live observation.
    /// </summary>
    [Fact]
    public void ResumeWhilePausedReportsTheStateItIsLeaving()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);
        _device.StartPrint(jobId: 302);
        _device.TryPause();

        IReadOnlyList<PlannedReply> replies = policy.Answer(JsonCommand(7, "RESUME_PRINT"), _device);

        using JsonDocument reply = Parse(replies[0]);
        reply.RootElement.GetProperty("event").GetString().Should().Be("FINISHED");
        reply.RootElement.GetProperty("state").GetString().Should().Be("PAUSED");
        _device.State.Should().Be(DeviceState.Printing);
    }

    /// <summary>
    /// SET_PRINTER_READY reports the <b>new</b> state, unlike job control - readiness is a local flag
    /// rather than a Marlin round trip, and the same capture shows <c>state=READY</c> on its ack. This
    /// pins the asymmetry so a later "consistency" cleanup cannot quietly erase it.
    /// </summary>
    [Fact]
    public void SetReadyReportsTheStateItIsEntering()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);

        IReadOnlyList<PlannedReply> replies = policy.Answer(JsonCommand(8, "SET_PRINTER_READY"), _device);

        using JsonDocument reply = Parse(replies[0]);
        reply.RootElement.GetProperty("state").GetString().Should().Be("READY",
            "confirmed on the wire - this one is not deferred the way job control is");
    }

    /// <summary>PAUSE_PRINT with nothing printing rejects with the JC macro's fixed reason.</summary>
    [Fact]
    public void PauseWhileIdleAnswersRejectedWithTheFirmwareReason()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);

        IReadOnlyList<PlannedReply> replies = policy.Answer(JsonCommand(5, "PAUSE_PRINT"), _device);

        using JsonDocument reply = Parse(replies[0]);
        reply.RootElement.GetProperty("event").GetString().Should().Be("REJECTED");
        reply.RootElement.GetProperty("reason").GetString().Should().Be("No print to pause");
    }

    /// <summary>SET_PRINTER_READY answers STATE_CHANGED, not FINISHED (planner.cpp:772-776).</summary>
    [Fact]
    public void SetReadyAnswersStateChanged()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);

        IReadOnlyList<PlannedReply> replies = policy.Answer(JsonCommand(6, "SET_PRINTER_READY"), _device);

        using JsonDocument reply = Parse(replies[0]);
        reply.RootElement.GetProperty("event").GetString().Should().Be("STATE_CHANGED");
        reply.RootElement.GetProperty("state").GetString().Should().Be("READY");
    }

    /// <summary>SET_PRINTER_IDLE outside Finished/Stopped rejects "Can't set idle now" - the real MK3.5's answer.</summary>
    [Fact]
    public void SetIdleMidSessionIsRejectedLikeTheRealPrinter()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);

        IReadOnlyList<PlannedReply> replies = policy.Answer(JsonCommand(7, "SET_PRINTER_IDLE"), _device);

        using JsonDocument reply = Parse(replies[0]);
        reply.RootElement.GetProperty("event").GetString().Should().Be("REJECTED");
        reply.RootElement.GetProperty("reason").GetString().Should().Be("Can't set idle now");
    }

    /// <summary>SEND_INFO answers a full INFO event carrying the command id (planner.cpp:735-740).</summary>
    [Fact]
    public void SendInfoAnswersInfoWithTheCommandId()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);

        IReadOnlyList<PlannedReply> replies = policy.Answer(JsonCommand(320, "SEND_INFO"), _device);

        using JsonDocument reply = Parse(replies[0]);
        reply.RootElement.GetProperty("event").GetString().Should().Be("INFO");
        reply.RootElement.GetProperty("command_id").GetUInt32().Should().Be(320);
        reply.RootElement.GetProperty("data").GetProperty("fingerprint").GetString().Should().Be(_identity.Fingerprint);
    }

    /// <summary>The same command id is never executed twice (planner.cpp:1103-1110).</summary>
    [Fact]
    public void ARepeatedCommandIdIsRefused()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);
        _device.StartPrint(jobId: 1);

        policy.Answer(JsonCommand(9, "PAUSE_PRINT"), _device);
        IReadOnlyList<PlannedReply> second = policy.Answer(JsonCommand(9, "RESUME_PRINT"), _device);

        using JsonDocument reply = Parse(second[0]);
        reply.RootElement.GetProperty("event").GetString().Should().Be("REJECTED");
        reply.RootElement.GetProperty("reason").GetString().Should().Be("Won't execute the same command multiple times");
        _device.State.Should().Be(DeviceState.Paused, "the duplicate must not have executed");
    }

    /// <summary>An unrecognised command name is "Unknown command"; garbage JSON is "Error parsing JSON".</summary>
    [Fact]
    public void UnknownAndGarbageCommandsEarnTheirDistinctReasons()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);

        IReadOnlyList<PlannedReply> unknown = policy.Answer(JsonCommand(10, "MAKE_COFFEE"), _device);
        IReadOnlyList<PlannedReply> garbage = policy.Answer(RawJsonFrame(11, "{{{"), _device);

        using JsonDocument unknownReply = Parse(unknown[0]);
        unknownReply.RootElement.GetProperty("reason").GetString().Should().Be("Unknown command");

        using JsonDocument garbageReply = Parse(garbage[0]);
        garbageReply.RootElement.GetProperty("reason").GetString().Should().Be("Error parsing JSON");
    }

    /// <summary>
    /// Gcode runs as a background command: ACCEPTED now, FINISHED after the execution time - and
    /// while it runs, another command is rejected busy while a resend of the same id is
    /// re-ACCEPTED (connect.cpp:224-240, planner.cpp:1094-1101).
    /// </summary>
    [Fact]
    public void GcodeOpensABusyWindowThatRejectsOthersAndReAcceptsItself()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System) { GcodeExecutionTime = TimeSpan.FromSeconds(30) };
        ServerCommandFrame gcode = new(ServerCommandKind.Gcode, 20, Encoding.UTF8.GetBytes("G28"));

        IReadOnlyList<PlannedReply> replies = policy.Answer(gcode, _device);

        replies.Should().HaveCount(2);
        Parse(replies[0]).RootElement.GetProperty("event").GetString().Should().Be("ACCEPTED");
        using JsonDocument finished = Parse(replies[1]);
        finished.RootElement.GetProperty("event").GetString().Should().Be("FINISHED");
        replies[1].Delay.Should().Be(TimeSpan.FromSeconds(30), "FINISHED comes when the gcode completes");

        IReadOnlyList<PlannedReply> other = policy.Answer(JsonCommand(21, "PAUSE_PRINT"), _device);
        using JsonDocument otherReply = Parse(other[0]);
        otherReply.RootElement.GetProperty("event").GetString().Should().Be("REJECTED");
        otherReply.RootElement.GetProperty("reason").GetString().Should().Be("Processing other command");

        IReadOnlyList<PlannedReply> resent = policy.Answer(gcode, _device);
        resent.Should().HaveCount(1);
        Parse(resent[0]).RootElement.GetProperty("event").GetString().Should().Be("ACCEPTED");
    }

    /// <summary>Debug frames are logged-and-dropped by the firmware; the fake answers nothing.</summary>
    [Fact]
    public void DebugFramesGetNoAnswer()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);
        ServerCommandFrame debug = new(ServerCommandKind.Debug, 30, Encoding.UTF8.GetBytes("hello"));

        policy.Answer(debug, _device).Should().BeEmpty();
    }

    private static ServerCommandFrame JsonCommand(uint id, string name)
    {
        return RawJsonFrame(id, $$"""{"command": "{{name}}"}""");
    }

    private static ServerCommandFrame RawJsonFrame(uint id, string json)
    {
        return new ServerCommandFrame(ServerCommandKind.Json, id, Encoding.UTF8.GetBytes(json));
    }

    private static JsonDocument Parse(PlannedReply reply)
    {
        return JsonDocument.Parse(reply.Payload!);
    }
}
