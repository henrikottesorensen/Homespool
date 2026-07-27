using System;
using System.Security.Cryptography;

namespace Homespool.FakePrinter;

/// <summary>
/// What a chunk did to the transfer.
/// </summary>
public enum ChunkOutcome
{
    /// <summary>Written; the transfer continues and wants another range.</summary>
    Accepted,

    /// <summary>The last byte arrived - the transfer succeeded.</summary>
    Completed,

    /// <summary>
    /// The transfer is dead. Firmware's inline engine has no retry for this
    /// (<c>DownloadStep::FailedRemote</c> goes straight to <c>State::Failed</c>,
    /// transfer.cpp:389-391), so neither does this.
    /// </summary>
    Failed,
}

/// <summary>
/// Which byte ranges a transfer asks for, and in what order
/// (<c>Transfer::init_download_order_if_needed</c>, transfer.cpp:225-236 at the pinned ref).
/// </summary>
public enum FakeDownloadOrder
{
    /// <summary>Straight through from byte 0. Everything except large plain gcode.</summary>
    Generic,

    /// <summary>
    /// The last <see cref="FakeTransfer.TailSize"/> bytes first, then the body from 0 - because plain
    /// gcode keeps its thumbnail and metadata at the end and <c>GcodeInfo</c> has to scan them before
    /// a preview or a print can start. Reaching the body costs a <b>RangeJump</b>, a full
    /// renegotiation with a fresh <c>file_id</c>.
    /// </summary>
    PlainGcodeTailFirst,
}

/// <summary>
/// The printer's side of one Connect-initiated inline download: which range to ask for next, and
/// what to do with the bytes that come back.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>transfers::Download</c>'s inline engine and the download order above it -
/// <c>inline_request()</c> (download.cpp:526-554) and <c>inline_chunk()</c> (download.cpp:556-577),
/// driven by <c>Transfer::step()</c>'s order handling (transfer.cpp:34-88, :161-223). The
/// validation rules are reproduced exactly, including that <b>none of them retries</b>: that is the
/// part worth having a fake for, because a real printer will not perform a failure on request.
/// </para>
/// <para>
/// Not thread-safe, like <see cref="FakeDevice"/>: it is owned by the connection's single read loop,
/// which is also where the firmware's planner drives its own copy of this.
/// </para>
/// <para>
/// One simplification against firmware, deliberate: the real <c>PartialFile</c> tracks validity so it
/// can survive a power cut and resume. This keeps the two ranges the download order actually consults
/// - a head growing from 0 and a tail running to the end - and nothing else, because the order this
/// models only ever produces those two.
/// </para>
/// </remarks>
public sealed class FakeTransfer
{
    /// <summary>
    /// The most one request may ask for (<c>INLINE_SEGMENT_SIZE</c>, download.cpp:65). Firmware's own
    /// comment explains the cap: smaller requests let the TCP buffers drain so commands can still get
    /// through mid-transfer.
    /// </summary>
    public const int InlineSegmentSize = 512 * 512;

    /// <summary>Bytes of tail fetched first under <see cref="FakeDownloadOrder.PlainGcodeTailFirst"/>
    /// (<c>TailSize</c>, transfer.hpp:57).</summary>
    public const int TailSize = 50_000;

    /// <summary>Below this, even plain gcode goes straight through
    /// (<c>MinimalFileSize</c>, transfer.hpp:67).</summary>
    public const int PlainGcodeMinimalFileSize = 512 * 1024;

    /// <summary>Requests start on a sector boundary (<c>PartialFile::SECTOR_SIZE</c>, and the
    /// rounding at transfer.cpp:205).</summary>
    public const int SectorSize = 512;

    private readonly byte[] _content;
    private readonly Func<uint> _fileIdSource;

    // The download order's phase. Generic never leaves the body phase.
    private bool _fetchingTail;

    // The "valid" ranges of the file so far, which is all the download order ever asks about.
    private long _headEnd;
    private long _tailStart = -1;
    private long _tailEnd = -1;

    // The current download - one negotiation. A RangeJump replaces all of these.
    private long _start;
    private long _end;
    private long _segmentEnd = -1;
    private bool _started;

    /// <summary>
    /// Starts a transfer for a <c>START_CONNECT_DOWNLOAD</c> we have accepted.
    /// </summary>
    /// <param name="hash">The server's token, quoted back on each first request.</param>
    /// <param name="teamId">Echoed from the command.</param>
    /// <param name="path">Destination on the printer, from the command's <c>path</c> argument.</param>
    /// <param name="totalSize">The command's <c>orig_size</c>.</param>
    /// <param name="transferId">This transfer's id, carried by its terminal events.</param>
    /// <param name="startCommandId">The command id that began it, reported as <c>start_cmd_id</c>.</param>
    /// <param name="order">
    /// Null selects the order the way firmware does, from the name and size. Passed explicitly only
    /// to force a <c>RangeJump</c> without a half-megabyte file.
    /// </param>
    /// <param name="fileIdSource">
    /// Supplies each negotiation's <c>file_id</c>. Defaults to a random 32-bit value, as
    /// <c>rand_u()</c> does; a test that wants to predict the id passes its own.
    /// </param>
    public FakeTransfer(string hash, ulong teamId, string path, long totalSize, int transferId,
        uint startCommandId, FakeDownloadOrder? order = null, Func<uint>? fileIdSource = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalSize);

        Hash = hash;
        TeamId = teamId;
        Path = path;
        TotalSize = totalSize;
        TransferId = transferId;
        StartCommandId = startCommandId;
        Order = order ?? ChooseOrder(path, totalSize);

        _content = new byte[totalSize];
        _fileIdSource = fileIdSource ?? RandomFileId;
        _fetchingTail = Order == FakeDownloadOrder.PlainGcodeTailFirst;

        BeginDownload();
    }

    /// <summary>The server's transfer token.</summary>
    public string Hash { get; }

    /// <summary>The team the command named.</summary>
    public ulong TeamId { get; }

    /// <summary>Destination path on the printer - the one the command gave us.</summary>
    public string Path { get; }

    /// <summary>Total bytes expected, from the command's <c>orig_size</c>.</summary>
    public long TotalSize { get; }

    /// <summary>This transfer's id.</summary>
    public int TransferId { get; }

    /// <summary>The <c>START_CONNECT_DOWNLOAD</c> that started it.</summary>
    public uint StartCommandId { get; }

    /// <summary>Which order the ranges are being fetched in.</summary>
    public FakeDownloadOrder Order { get; }

    /// <summary>The current negotiation's nonce. Changes on a <c>RangeJump</c>.</summary>
    public uint FileId { get; private set; }

    /// <summary>Every byte received so far, at its own offset. Complete once <see cref="IsComplete"/>.</summary>
    public ReadOnlyMemory<byte> Content => _content;

    /// <summary>Bytes received - not the same as the highest offset written, under a tail-first order.</summary>
    public long ValidSize => _headEnd + (_tailStart < 0 ? 0 : _tailEnd - _tailStart);

    /// <summary>Whether every byte has arrived.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Whether a chunk killed it. Terminal - nothing resumes from here.</summary>
    public bool HasFailed { get; private set; }

    /// <summary>How many negotiations have been opened; 1 unless a <c>RangeJump</c> happened.</summary>
    public int NegotiationCount { get; private set; }

    /// <summary>
    /// Picks the order from the file's name and size the way <c>init_download_order_if_needed</c>
    /// does: only <i>plain</i> gcode (not bgcode) at or above half a megabyte fetches its tail first.
    /// </summary>
    public static FakeDownloadOrder ChooseOrder(string path, long totalSize)
    {
        return IsPlainGcode(path) && totalSize >= PlainGcodeMinimalFileSize
            ? FakeDownloadOrder.PlainGcodeTailFirst
            : FakeDownloadOrder.Generic;
    }

    /// <summary>
    /// <c>filename_is_plain_gcode</c> (filename_type.cpp:10-16) - four extensions, case-insensitive,
    /// and <b>not</b> the binary ones, which is the whole point of the distinction here.
    /// </summary>
    public static bool IsPlainGcode(string path)
    {
        return path.EndsWith(".g", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gc", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gco", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The next range to ask for, or null when one is already outstanding, the transfer is over, or
    /// the current negotiation has delivered everything it covers.
    /// </summary>
    /// <remarks>
    /// The re-arm condition is firmware's (download.cpp:531): a request goes out either because none
    /// ever has for this negotiation, or because the previous segment is fully delivered and it was
    /// not the last one. Strictly one in flight, always.
    /// </remarks>
    public InlineRequest? NextRequest()
    {
        if (IsComplete || HasFailed)
        {
            return null;
        }

        bool segmentDrained = _start > _segmentEnd && _segmentEnd != _end;

        if (_started && !segmentDrained)
        {
            return null;
        }

        _segmentEnd = Math.Min(_start + InlineSegmentSize - 1, _end);

        InlineRequestDetails? details = null;

        if (!_started)
        {
            // Sent once per negotiation, not once per transfer: a RangeJump re-sends them, which is
            // how the server learns the new file_id belongs to a transfer it already knows.
            details = new InlineRequestDetails(Hash, TeamId, TransferId);
            _started = true;
        }

        return new InlineRequest(FileId, _start, _segmentEnd, details);
    }

    /// <summary>
    /// Takes one chunk from the server, applying <c>inline_chunk</c>'s validation
    /// (download.cpp:556-577) before a byte of it is kept.
    /// </summary>
    /// <param name="fileId">The id from the chunk's header.</param>
    /// <param name="data">The payload; empty is the server's deliberate "I failed" signal.</param>
    /// <remarks>
    /// The three rejections are exactly firmware's, and each one ends the transfer outright: a
    /// <c>file_id</c> that is not the current negotiation's, an empty payload, and a payload that
    /// would run past the negotiation's end. Note the overrun is checked against the <b>download's</b>
    /// end rather than the segment's, so a chunk may legally cross a segment boundary.
    /// </remarks>
    public ChunkOutcome AcceptChunk(uint fileId, ReadOnlySpan<byte> data)
    {
        if (IsComplete || HasFailed)
        {
            // status != DownloadStep::Continue - a late chunk for a transfer that is already over.
            return Fail();
        }

        if (fileId != FileId || data.Length == 0 || _start + data.Length > _end + 1)
        {
            return Fail();
        }

        data.CopyTo(_content.AsSpan((int)_start));
        Record(_start, data.Length);
        _start += data.Length;

        if (ValidSize >= TotalSize)
        {
            IsComplete = true;

            return ChunkOutcome.Completed;
        }

        if (_fetchingTail && HasValidTail())
        {
            // The order's RangeJump arm (transfer.cpp:44-49): the tail is in, so renegotiate from the
            // top of the file. restart_download allocates a fresh file_id and re-sends the details.
            _fetchingTail = false;
            BeginDownload();
        }

        return ChunkOutcome.Accepted;
    }

    private static uint RandomFileId()
    {
        return BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(sizeof(uint)));
    }

    private static long AlignDown(long offset)
    {
        return offset / SectorSize * SectorSize;
    }

    /// <summary>
    /// Opens a negotiation - the first, or the one a <c>RangeJump</c> asks for. Both the starting
    /// offset and the end come from <c>restart_download</c> (transfer.cpp:203-220).
    /// </summary>
    private void BeginDownload()
    {
        if (_fetchingTail)
        {
            // get_next_offset's tail arm: no valid tail yet, so start TailSize from the end, rounded
            // down to a sector.
            _start = AlignDown(Math.Max(0, TotalSize - TailSize));
            _end = TotalSize - 1;
        }
        else
        {
            // The body arm: continue after the valid head, which is byte 0 until one exists. When a
            // tail is already in place and runs to the end of the file, stop where it starts rather
            // than fetching it twice - the two ranges then tile the file exactly.
            _start = _headEnd;
            _end = _tailStart >= 0 && _tailEnd == TotalSize && _start < _tailStart
                ? _tailStart - 1
                : TotalSize - 1;
        }

        FileId = _fileIdSource();
        _segmentEnd = -1;
        _started = false;
        NegotiationCount++;
    }

    /// <summary>
    /// <c>has_valid_tail(TailSize)</c>: a tail that reaches the end of the file and is at least that
    /// long. What flips the plain-gcode order out of its first phase.
    /// </summary>
    private bool HasValidTail()
    {
        return _tailStart >= 0 && _tailEnd == TotalSize && _tailEnd - _tailStart >= TailSize;
    }

    private void Record(long offset, int length)
    {
        if (offset == _headEnd)
        {
            _headEnd = offset + length;

            return;
        }

        if (_tailStart < 0)
        {
            _tailStart = offset;
            _tailEnd = offset + length;

            return;
        }

        _tailEnd = Math.Max(_tailEnd, offset + length);
    }

    private ChunkOutcome Fail()
    {
        HasFailed = true;

        return ChunkOutcome.Failed;
    }
}
