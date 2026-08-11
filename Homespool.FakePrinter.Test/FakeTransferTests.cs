using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The printer's side of an inline download against the firmware it models: <c>inline_request</c>'s
/// segmenting and one-in-flight rule (download.cpp:526-554), <c>inline_chunk</c>'s three rejections
/// (download.cpp:556-577), and the tail-first download order that makes a large plain gcode
/// renegotiate mid-transfer (transfer.cpp:34-88, :203-220).
/// </summary>
public class FakeTransferTests
{
    /// <summary>The first request of a negotiation carries hash/team/transfer id; the rest do not.</summary>
    [Fact]
    public void OnlyTheFirstRequestOfANegotiationCarriesTheDetails()
    {
        byte[] source = Content(FakeTransfer.InlineSegmentSize * 2);
        FakeTransfer transfer = Begin(source.Length, "/usb/model.bgcode");

        InlineRequest first = transfer.NextRequest()!;
        first.Details.Should().NotBeNull();
        first.Details!.Hash.Should().Be("hash-token");
        first.Details.TeamId.Should().Be(7);
        first.Details.TransferId.Should().Be(transfer.TransferId);

        Deliver(transfer, source, first);

        transfer.NextRequest()!.Details.Should().BeNull("firmware sends the details block once per negotiation");
    }

    /// <summary>A request never asks for more than INLINE_SEGMENT_SIZE, and end is inclusive.</summary>
    [Fact]
    public void SegmentsAreCappedAtTheInlineSegmentSize()
    {
        byte[] source = Content(FakeTransfer.InlineSegmentSize + 1000);
        FakeTransfer transfer = Begin(source.Length, "/usb/model.bgcode");

        InlineRequest first = transfer.NextRequest()!;

        first.Start.Should().Be(0);
        first.End.Should().Be(FakeTransfer.InlineSegmentSize - 1, "end is inclusive, so the length is end - start + 1");

        Deliver(transfer, source, first);

        InlineRequest second = transfer.NextRequest()!;
        second.Start.Should().Be(FakeTransfer.InlineSegmentSize);
        second.End.Should().Be(source.Length - 1);
    }

    /// <summary>No second request until the outstanding segment is fully delivered.</summary>
    [Fact]
    public void OnlyOneRequestIsOutstandingAtATime()
    {
        byte[] source = Content(FakeTransfer.InlineSegmentSize * 2);
        FakeTransfer transfer = Begin(source.Length, "/usb/model.bgcode");

        InlineRequest first = transfer.NextRequest()!;

        transfer.NextRequest().Should().BeNull("the previous segment is still outstanding");

        transfer.AcceptChunk(first.FileId, source.AsSpan(0, 1000)).Should().Be(ChunkOutcome.Accepted);
        transfer.NextRequest().Should().BeNull("a partly delivered segment is still outstanding");

        Deliver(transfer, source, first, from: 1000);
        transfer.NextRequest().Should().NotBeNull();
    }

    /// <summary>A chunk for a different file_id kills the transfer outright - firmware's safety check.</summary>
    [Fact]
    public void AChunkForAnotherFileIdKillsTheTransfer()
    {
        byte[] source = Content(4096);
        FakeTransfer transfer = Begin(source.Length, "/usb/model.bgcode");
        InlineRequest request = transfer.NextRequest()!;

        transfer.AcceptChunk(request.FileId + 1, source).Should().Be(ChunkOutcome.Failed);

        transfer.HasFailed.Should().BeTrue();
        transfer.NextRequest().Should().BeNull("there is no retry for a FailedRemote");
    }

    /// <summary>An empty chunk is the server's "I failed" signal, and ends the transfer.</summary>
    [Fact]
    public void AnEmptyChunkEndsTheTransfer()
    {
        FakeTransfer transfer = Begin(4096, "/usb/model.bgcode");
        InlineRequest request = transfer.NextRequest()!;

        transfer.AcceptChunk(request.FileId, ReadOnlySpan<byte>.Empty).Should().Be(ChunkOutcome.Failed);
    }

    /// <summary>Bytes past the negotiation's end fail it, rather than being truncated.</summary>
    [Fact]
    public void AnOverlongChunkKillsTheTransfer()
    {
        byte[] source = Content(4096);
        FakeTransfer transfer = Begin(source.Length, "/usb/model.bgcode");
        InlineRequest request = transfer.NextRequest()!;

        transfer.AcceptChunk(request.FileId, new byte[source.Length + 1]).Should().Be(ChunkOutcome.Failed);
    }

    /// <summary>
    /// The overrun check is against the download's end, not the segment's - so a chunk that spans a
    /// segment boundary is legal. Easy to get wrong in the direction that breaks a live transfer.
    /// </summary>
    [Fact]
    public void AChunkMayCrossASegmentBoundary()
    {
        byte[] source = Content(FakeTransfer.InlineSegmentSize + 1000);
        FakeTransfer transfer = Begin(source.Length, "/usb/model.bgcode");
        InlineRequest request = transfer.NextRequest()!;

        transfer.AcceptChunk(request.FileId, source).Should().Be(ChunkOutcome.Completed);

        transfer.Content.ToArray().Should().Equal(source);
    }

    /// <summary>Once dead, later chunks stay rejected rather than reviving it.</summary>
    [Fact]
    public void AFailedTransferStaysFailed()
    {
        byte[] source = Content(4096);
        FakeTransfer transfer = Begin(source.Length, "/usb/model.bgcode");
        InlineRequest request = transfer.NextRequest()!;

        transfer.AcceptChunk(request.FileId, ReadOnlySpan<byte>.Empty);

        transfer.AcceptChunk(request.FileId, source).Should().Be(ChunkOutcome.Failed);
    }

    /// <summary>A sequential transfer reassembles the file byte for byte.</summary>
    [Fact]
    public void TheWholeFileArrivesIntact()
    {
        byte[] source = Content(700_000);
        FakeTransfer transfer = Begin(source.Length, "/usb/model.bgcode");

        Drive(transfer, source);

        transfer.IsComplete.Should().BeTrue();
        transfer.Content.ToArray().Should().Equal(source);
        transfer.NegotiationCount.Should().Be(1, "bgcode takes the generic order - no jump");
    }

    /// <summary>
    /// Large plain gcode fetches its tail first and then renegotiates from byte 0 - the RangeJump.
    /// This is the ordinary case, not an exotic one: every plain gcode over 512 KiB does it.
    /// </summary>
    [Fact]
    public void LargePlainGcodeFetchesItsTailThenRangeJumps()
    {
        byte[] source = Content(600 * 1024);
        FakeTransfer transfer = Begin(source.Length, "/usb/model.gcode");

        transfer.Order.Should().Be(FakeDownloadOrder.PlainGcodeTailFirst);

        InlineRequest first = transfer.NextRequest()!;
        long expectedTailStart = (source.Length - FakeTransfer.TailSize) / FakeTransfer.SectorSize * FakeTransfer.SectorSize;
        first.Start.Should().Be(expectedTailStart, "the tail starts TailSize from the end, rounded down to a sector");
        first.End.Should().Be(source.Length - 1);

        uint tailFileId = first.FileId;
        Deliver(transfer, source, first);

        InlineRequest afterJump = transfer.NextRequest()!;
        afterJump.FileId.Should().NotBe(tailFileId, "restart_download allocates a fresh file_id");
        afterJump.Details.Should().NotBeNull("the renegotiation re-sends the hash, which is how the server matches it");
        afterJump.Start.Should().Be(0);
        transfer.NegotiationCount.Should().Be(2);
    }

    /// <summary>The two negotiations cover the file exactly once - no gap, no re-fetch.</summary>
    [Fact]
    public void ARangeJumpsTwoNegotiationsTileTheFileExactly()
    {
        byte[] source = Content(600 * 1024);
        FakeTransfer transfer = Begin(source.Length, "/usb/model.gcode");

        List<InlineRequest> requests = Drive(transfer, source);

        transfer.IsComplete.Should().BeTrue();
        transfer.Content.ToArray().Should().Equal(source);

        long requested = requests.Sum(request => request.End - request.Start + 1);
        requested.Should().Be(source.Length, "every byte is asked for once and only once");
    }

    /// <summary>
    /// The order is chosen the way firmware chooses it - plain gcode only, and only above the size
    /// where scanning the tail for a preview is worth a renegotiation.
    /// </summary>
    [Theory]
    [InlineData("/usb/model.gcode", 600 * 1024, FakeDownloadOrder.PlainGcodeTailFirst)]
    [InlineData("/usb/model.gco", 600 * 1024, FakeDownloadOrder.PlainGcodeTailFirst)]
    [InlineData("/usb/model.gcode", 400 * 1024, FakeDownloadOrder.Generic)]
    [InlineData("/usb/model.bgcode", 600 * 1024, FakeDownloadOrder.Generic)]
    [InlineData("/usb/model.bgc", 600 * 1024, FakeDownloadOrder.Generic)]
    public void TheOrderFollowsTheNameAndSize(string path, long size, FakeDownloadOrder expected)
    {
        FakeTransfer.ChooseOrder(path, size).Should().Be(expected);
    }

    /// <summary>A forced tail-first order lets a test provoke a jump without a half-megabyte file.</summary>
    [Fact]
    public void TheOrderCanBeForcedForASmallFile()
    {
        byte[] source = Content(200_000);
        FakeTransfer transfer = Begin(source.Length, "/usb/small.gcode", FakeDownloadOrder.PlainGcodeTailFirst);

        Drive(transfer, source);

        transfer.NegotiationCount.Should().Be(2);
        transfer.Content.ToArray().Should().Equal(source);
    }

    private static byte[] Content(int length)
    {
        // Deterministic but position-dependent, so a chunk written at the wrong offset shows up as a
        // content mismatch rather than passing by accident.
        byte[] content = new byte[length];

        for (int i = 0; i < length; i++)
        {
            content[i] = (byte)((i * 31) % 251);
        }

        return content;
    }

    private static FakeTransfer Begin(long size, string path, FakeDownloadOrder? order = null)
    {
        return new FakeTransfer("hash-token", 7, path, size, transferId: 42, startCommandId: 9, order,
                                fileIdSource: () => BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(sizeof(uint))));
    }

    /// <summary>Serves one whole request in a single chunk.</summary>
    private static void Deliver(FakeTransfer transfer, byte[] source, InlineRequest request, long from = 0)
    {
        long start = request.Start + from;
        int length = (int)(request.End - start + 1);

        transfer.AcceptChunk(request.FileId, source.AsSpan((int)start, length));
    }

    /// <summary>Serves the transfer to completion, returning every request it made.</summary>
    private static List<InlineRequest> Drive(FakeTransfer transfer, byte[] source)
    {
        List<InlineRequest> requests = [];

        while (transfer.NextRequest() is { } request)
        {
            requests.Add(request);
            Deliver(transfer, source, request);
        }

        return requests;
    }
}
