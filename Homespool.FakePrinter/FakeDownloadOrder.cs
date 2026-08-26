namespace Homespool.FakePrinter;

/// <summary>
/// Which byte ranges a transfer asks for, and in what order
/// (<c>Transfer::init_download_order_if_needed</c>, transfer.cpp:225-236 at the pinned ref).
/// </summary>
public enum FakeDownloadOrder
{
    Undefined = 0,

    /// <summary>Straight through from byte 0. Everything except large plain gcode.</summary>
    Generic = 1,

    /// <summary>
    /// The last <see cref="FakeTransfer.TailSize"/> bytes first, then the body from 0 - because plain
    /// gcode keeps its thumbnail and metadata at the end and <c>GcodeInfo</c> has to scan them before
    /// a preview or a print can start. Reaching the body costs a <b>RangeJump</b>, a full
    /// renegotiation with a fresh <c>file_id</c>.
    /// </summary>
    PlainGcodeTailFirst = 2,
}
