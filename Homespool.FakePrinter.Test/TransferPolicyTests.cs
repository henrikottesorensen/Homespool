using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The default policy's transfer arms: <c>START_CONNECT_DOWNLOAD</c> answered with
/// <c>TRANSFER_INFO</c> and a first range request (planner.cpp:801-824), and <c>'T'</c> chunks driving
/// the download to its terminal event.
/// </summary>
public class TransferPolicyTests
{
    private readonly PrinterIdentity _identity = PrinterIdentity.CreateRandom();
    private readonly FakeDevice _device = new();

    /// <summary>The command is answered with TRANSFER_INFO - not FINISHED - and then a range request.</summary>
    [Fact]
    public void StartDownloadIsAnsweredWithTransferInfoThenARequest()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);

        IReadOnlyList<PlannedReply> replies = policy.Answer(StartDownload(11, size: 4096), _device);

        replies.Should().HaveCount(2);

        using JsonDocument info = Parse(replies[0]);
        info.RootElement.GetProperty("event").GetString().Should().Be("TRANSFER_INFO");
        info.RootElement.GetProperty("command_id").GetUInt32().Should().Be(11);

        using JsonDocument request = Parse(replies[1]);
        request.RootElement.GetProperty("transfer").GetString().Should().Be("inline");
        request.RootElement.GetProperty("hash").GetString().Should().Be("hash-token");
        request.RootElement.GetProperty("start").GetInt64().Should().Be(0);
        request.RootElement.GetProperty("end").GetInt64().Should().Be(4095);
        request.RootElement.GetProperty("chunk").GetInt32().Should().Be(4096);
    }

    /// <summary>
    /// <c>start_cmd_id</c> sits inside <c>data</c> and <c>transfer_id</c> at the root. The pairing is
    /// easy to invert, and a root-level <c>start_cmd_id</c> would bind to null forever without failing.
    /// </summary>
    [Fact]
    public void TransferInfoPutsStartCommandIdInDataAndTransferIdAtTheRoot()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);

        IReadOnlyList<PlannedReply> replies = policy.Answer(StartDownload(11, size: 4096), _device);

        using JsonDocument info = Parse(replies[0]);
        info.RootElement.GetProperty("data").GetProperty("start_cmd_id").GetUInt32().Should().Be(11);
        info.RootElement.GetProperty("data").GetProperty("type").GetString().Should().Be("FROM_CONNECT");
        info.RootElement.GetProperty("data").GetProperty("path").GetString().Should().Be("/usb/model.bgcode");
        info.RootElement.TryGetProperty("transfer_id", out JsonElement transferId).Should().BeTrue();
        transferId.GetInt32().Should().Be(_device.Transfer!.TransferId);
    }

    /// <summary>The transfer slot is single and system-wide, so a second download is refused.</summary>
    [Fact]
    public void ASecondDownloadIsRejectedWhileOneRuns()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);
        policy.Answer(StartDownload(11, size: 4096), _device);

        IReadOnlyList<PlannedReply> replies = policy.Answer(StartDownload(12, size: 4096), _device);

        using JsonDocument rejection = Parse(replies[0]);
        rejection.RootElement.GetProperty("event").GetString().Should().Be("REJECTED");
        rejection.RootElement.GetProperty("reason").GetString().Should().Be("Another transfer in progress");
        rejection.RootElement.GetProperty("machine_reason").GetString().Should().Be("TRANSFER_IN_PROGRESS");
    }

    /// <summary>Paths the printer would refuse, refused the same way and for the same reasons.</summary>
    [Theory]
    [InlineData("/home/model.bgcode", "Not allowed outside /usb")]
    [InlineData("/usb/../etc/model.bgcode", "Not allowed outside /usb")]
    [InlineData("/usb/model.txt", "Unsupported file type")]
    public void ARefusedPathIsRejectedBeforeTheSlotIsTaken(string path, string reason)
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);

        IReadOnlyList<PlannedReply> replies = policy.Answer(StartDownload(11, size: 4096, path: path), _device);

        using JsonDocument rejection = Parse(replies[0]);
        rejection.RootElement.GetProperty("reason").GetString().Should().Be(reason);
        _device.Transfer.Should().BeNull("the path is checked before the transfer slot is taken");
    }

    /// <summary>All four kwargs are required; firmware rejects the command outright without them.</summary>
    [Fact]
    public void AnIncompleteCommandIsRejected()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);
        ServerCommandFrame frame = new(ServerCommandKind.Json, 11, Encoding.UTF8.GetBytes(
                                           """{"command": "START_CONNECT_DOWNLOAD", "args": [], "kwargs": {"path": "/usb/model.bgcode"}}"""));

        IReadOnlyList<PlannedReply> replies = policy.Answer(frame, _device);

        using JsonDocument rejection = Parse(replies[0]);
        rejection.RootElement.GetProperty("reason").GetString().Should().Be("Missing or broken parameters");
    }

    /// <summary>A partial chunk is answered with nothing; the outstanding request still stands.</summary>
    [Fact]
    public void APartlyDeliveredSegmentDrawsNoNewRequest()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);
        IReadOnlyList<PlannedReply> start = policy.Answer(StartDownload(11, size: 8192), _device);
        uint fileId = FileIdOf(start[1]);

        policy.Answer(Chunk(fileId, new byte[1000]), _device).Should().BeEmpty();
    }

    /// <summary>The last chunk produces TRANSFER_FINISHED and then the FILE_INFO for the new file.</summary>
    [Fact]
    public void TheFinalChunkProducesTransferFinishedThenFileInfo()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);
        IReadOnlyList<PlannedReply> start = policy.Answer(StartDownload(11, size: 4096), _device);
        uint fileId = FileIdOf(start[1]);
        int transferId = _device.Transfer!.TransferId;

        IReadOnlyList<PlannedReply> replies = policy.Answer(Chunk(fileId, new byte[4096]), _device);

        replies.Should().HaveCount(2);

        using JsonDocument finished = Parse(replies[0]);
        finished.RootElement.GetProperty("event").GetString().Should().Be("TRANSFER_FINISHED");
        finished.RootElement.GetProperty("transfer_id").GetInt32().Should().Be(transferId);
        finished.RootElement.GetProperty("data").GetProperty("start_cmd_id").GetUInt32().Should().Be(11);
        finished.RootElement.TryGetProperty("command_id", out _).Should().BeFalse("terminal events are unsolicited");

        using JsonDocument file = Parse(replies[1]);
        file.RootElement.GetProperty("event").GetString().Should().Be("FILE_INFO");
        file.RootElement.GetProperty("data").GetProperty("path").GetString().Should().Be("/usb/model.bgcode");
        file.RootElement.GetProperty("data").GetProperty("display_name").GetString().Should().Be("model.bgcode");
        file.RootElement.GetProperty("data").GetProperty("type").GetString().Should().Be("PRINT_FILE");
        file.RootElement.GetProperty("data").GetProperty("size").GetInt64().Should().Be(4096);

        _device.Transfer.Should().BeNull("the slot is released when the transfer ends");
    }

    /// <summary>A chunk the printer refuses ends the transfer as ABORTED, with no retry.</summary>
    [Fact]
    public void ARefusedChunkProducesTransferAborted()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);
        IReadOnlyList<PlannedReply> start = policy.Answer(StartDownload(11, size: 4096), _device);
        uint fileId = FileIdOf(start[1]);

        IReadOnlyList<PlannedReply> replies = policy.Answer(Chunk(fileId, []), _device);

        replies.Should().HaveCount(1);
        using JsonDocument aborted = Parse(replies[0]);
        aborted.RootElement.GetProperty("event").GetString().Should().Be("TRANSFER_ABORTED");
        _device.Transfer.Should().BeNull();
    }

    /// <summary>A stray chunk with no transfer running is blackholed, not answered.</summary>
    [Fact]
    public void AChunkWithNoTransferRunningIsIgnored()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System);

        policy.Answer(Chunk(1234, new byte[16]), _device).Should().BeEmpty();
    }

    /// <summary>
    /// Chunks bypass the one-in-flight command guard, so a transfer keeps moving while a background
    /// gcode command holds the command slot (connect.cpp:468).
    /// </summary>
    [Fact]
    public void ChunksAreServedWhileABackgroundCommandIsBusy()
    {
        FirmwareFaithfulPolicy policy = new(_identity, TimeProvider.System) { GcodeExecutionTime = TimeSpan.FromMinutes(1) };
        IReadOnlyList<PlannedReply> start = policy.Answer(StartDownload(11, size: 8192), _device);
        uint fileId = FileIdOf(start[1]);

        policy.Answer(new ServerCommandFrame(ServerCommandKind.Gcode, 12, Encoding.UTF8.GetBytes("G28")), _device);

        // A J command now would be rejected as "Processing other command"; a chunk is not.
        IReadOnlyList<PlannedReply> replies = policy.Answer(Chunk(fileId, new byte[8192]), _device);

        replies.Should().HaveCount(2);
        Parse(replies[0]).RootElement.GetProperty("event").GetString().Should().Be("TRANSFER_FINISHED");
    }

    private static ServerCommandFrame StartDownload(uint id, long size, string path = "/usb/model.bgcode")
    {
        // Built the way CommandWireEncoder builds it, so the payload shape under test is the one the
        // server really sends rather than a hand-written approximation of it.
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            command = "START_CONNECT_DOWNLOAD",
            args = Array.Empty<object>(),
            kwargs = new { path, team_id = 7, hash = "hash-token", orig_size = size },
        });

        return new ServerCommandFrame(ServerCommandKind.Json, id, json);
    }

    private static ServerCommandFrame Chunk(uint fileId, byte[] payload)
    {
        return new ServerCommandFrame(ServerCommandKind.TransferChunk, fileId, payload);
    }

    private static uint FileIdOf(PlannedReply request)
    {
        using JsonDocument document = Parse(request);

        return document.RootElement.GetProperty("file_id").GetUInt32();
    }

    private static JsonDocument Parse(PlannedReply reply)
    {
        return JsonDocument.Parse(reply.Payload!);
    }
}
