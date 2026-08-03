using System;

using Homespool.Model;

namespace Homespool.Host.Services;

/// <summary>
/// The rules deciding what the loop does next for one printer, from state alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and separate from the service that acts on it</b>, because this is where the rule that
/// protects a finished print lives and that rule has no backstop underneath it: firmware will
/// cheerfully start a print onto a bed that still holds the last one
/// (<c>notes/print-queue.md</c>, "Firmware will start a print onto a finished part"). A decision
/// function with no I/O can be tested exhaustively over every printer state, which is the only way
/// that rule gets held down.
/// </para>
/// <para>
/// It decides <i>what</i>, never <i>whether it worked</i>. Retry classification belongs with the
/// caller that has the printer's answer in hand.
/// </para>
/// <para>
/// <b>Rules rather than a policy</b>, though the codebase has a <c>*Policy</c> family
/// (<c>CommandAnswerPolicy</c> and its implementations). Every one of those is a substitutable
/// strategy, swapped out in tests; this must not be. It is the single gate standing between a queue
/// and a print started onto somebody's finished part, and a name inviting someone to substitute it
/// would invite exactly the wrong change.
/// </para>
/// </remarks>
public static class QueueRules
{
    /// <summary>
    /// The states a printer is available for work in. <b>Exactly one</b>, and deliberately not the
    /// four firmware would accept.
    /// </summary>
    /// <remarks>
    /// <c>Ready</c> is firmware's own flag - <c>Idle</c> plus somebody having said "you may take
    /// work" - and only a person sets it, from the app after a confirmation or from the printer's own
    /// menu. <c>Idle</c>, <c>Finished</c> and <c>Stopped</c> are all states a printer can sit in with
    /// a part still on the bed, and firmware's <c>remote_print_ready</c> accepts a print in three of
    /// them. This is the narrower rule, and the difference between the two is the whole safety margin.
    /// </remarks>
    public static bool IsAvailable(PrinterStatus status)
    {
        return status == PrinterStatus.Ready;
    }

    /// <summary>Decides the next action for one printer.</summary>
    /// <param name="situation">Everything the decision depends on, gathered by the caller.</param>
    public static QueueAction Decide(QueueSnapshot situation)
    {
        ArgumentNullException.ThrowIfNull(situation);

        if (!situation.Connected)
        {
            // Powered off, or the socket is gone. The queue simply waits - there is nothing to do and
            // nothing to report, which is why this is Nothing rather than Wait.
            return QueueAction.Nothing;
        }

        if (situation.Head is not { } head)
        {
            return QueueAction.Nothing;
        }

        if (situation.TransferInFlight)
        {
            // Firmware has one system-wide transfer slot, so there is nothing useful to start while
            // one is running - including for a different file.
            return QueueAction.Wait(QueueWaitReason.Transferring);
        }

        if (!head.FileHasArrived)
        {
            // Deliberately not gated on the printer being available: a transfer runs alongside a
            // print, which is proven on hardware and is the point of pipelining - the next job's
            // bytes move while the current one prints, so the gap between prints disappears.
            return QueueAction.Transfer(head);
        }

        if (head.PrinterPath is null)
        {
            // Arrived, but the FILE_INFO that names it has not been seen. Printing the path we sent
            // rather than the one the printer reported is the guess this refuses to make.
            return QueueAction.Wait(QueueWaitReason.AwaitingPrinterPath);
        }

        if (situation.PrintInFlight)
        {
            // Commanded already; the printer just has not caught up. See PrintInFlight - this is the
            // window where it still says READY.
            return QueueAction.Wait(QueueWaitReason.PrintStarting);
        }

        if (!IsAvailable(situation.Status))
        {
            // Includes Finished and Stopped, where the printer would accept the command. Paused and
            // Attention are stalls a person clears; the loop never resumes and never cancels.
            return QueueAction.Wait(QueueWaitReason.PrinterNotAvailable);
        }

        return QueueAction.Print(head);
    }
}

/// <summary>Everything <see cref="QueueRules.Decide"/> looks at, gathered at one moment.</summary>
/// <remarks>
/// A snapshot rather than a "state", because it spans three subjects - the connection, the printer
/// and the queue - and only one of them is the queue's. Naming it for the smallest part it carries
/// would also put a second meaning of "state" next to <c>PrinterLiveState</c> and
/// <see cref="PrinterStatus"/>, which is the most overloaded word in this codebase already.
/// </remarks>
/// <param name="Connected">Whether the printer has a live WebSocket right now.</param>
/// <param name="Status">Its last-known state, from <c>PrinterLiveState</c>.</param>
/// <param name="Head">The queue's first entry, or null when the queue is empty.</param>
/// <param name="TransferInFlight">Whether this printer is already pulling a file from us.</param>
/// <param name="PrintInFlight">
/// Whether a print of ours is open on this printer - commanded and not yet ended.
/// <para>
/// <b>Load-bearing for about three seconds, which is exactly long enough to matter.</b> A printer
/// accepts <c>START_PRINT</c> and keeps reporting <c>READY</c> until it has finished preview-init
/// and heating - measured at 3.1 s in the Core One capture. Without this the loop would see a ready
/// printer with a queue and command a second print into that gap.
/// </para>
/// </param>
public sealed record QueueSnapshot(bool Connected, PrinterStatus Status, QueueHead? Head, bool TransferInFlight,
    bool PrintInFlight = false);

/// <summary>The entry at the front of a printer's queue, with what is known about its file.</summary>
/// <param name="QueuedPrintId">The queue entry.</param>
/// <param name="PrintFileId">The file it wants.</param>
/// <param name="FileName">Its name, for logs and for matching the printer's <c>FILE_INFO</c>.</param>
/// <param name="FileHasArrived">Whether the bytes are believed to be on the drive.</param>
/// <param name="PrinterPath">What the printer calls it, once a <c>FILE_INFO</c> has said.</param>
public sealed record QueueHead(long QueuedPrintId, long PrintFileId, string FileName, bool FileHasArrived,
    string? PrinterPath);

/// <summary>What the loop decided to do.</summary>
public sealed record QueueAction
{
    private QueueAction()
    {
    }

    /// <summary>Nothing to do, and nothing being waited for.</summary>
    public static QueueAction Nothing { get; } = new() { Kind = QueueActionKind.Nothing };

    public required QueueActionKind Kind { get; init; }

    /// <summary>The entry acted on, for <see cref="QueueActionKind.Transfer"/> and
    /// <see cref="QueueActionKind.Print"/>.</summary>
    public QueueHead? Head { get; init; }

    /// <summary>Why the loop is waiting, for <see cref="QueueActionKind.Wait"/>.</summary>
    public QueueWaitReason Reason { get; init; }

    public static QueueAction Transfer(QueueHead head)
    {
        return new QueueAction { Kind = QueueActionKind.Transfer, Head = head };
    }

    public static QueueAction Print(QueueHead head)
    {
        return new QueueAction { Kind = QueueActionKind.Print, Head = head };
    }

    public static QueueAction Wait(QueueWaitReason reason)
    {
        return new QueueAction { Kind = QueueActionKind.Wait, Reason = reason };
    }
}

public enum QueueActionKind
{
    /// <summary>No queue, or no connection. Not a stall - there is genuinely nothing to do.</summary>
    Nothing = 0,

    /// <summary>Send the head's file to the printer.</summary>
    Transfer,

    /// <summary>Start printing the head.</summary>
    Print,

    /// <summary>Something is in the way, and it will clear on its own or with a person's help.</summary>
    Wait,
}

/// <summary>Why the loop is holding, so a log line or a UI can say something true.</summary>
public enum QueueWaitReason
{
    Unknown = 0,

    /// <summary>A file is being pulled from us; firmware allows only one transfer at a time.</summary>
    Transferring,

    /// <summary>The bytes arrived but no <c>FILE_INFO</c> has named the path to print.</summary>
    AwaitingPrinterPath,

    /// <summary>
    /// The printer is not <c>Ready</c>. Includes a finished print nobody has cleared, which is the
    /// case with no backstop under it.
    /// </summary>
    PrinterNotAvailable,

    /// <summary>
    /// A print has been commanded and the printer has not reported itself printing yet - the few
    /// seconds in which it still says <c>READY</c>.
    /// </summary>
    PrintStarting,
}
