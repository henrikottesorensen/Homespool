using System;

using Homespool.Model;

namespace Homespool.Host.Queue;

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

        if (situation.HoldReason is { } hold)
        {
            // Ahead of the transfer branch, because a blocked file is precisely one that would
            // otherwise be reported as about to be sent. The advancer discovers the block and records
            // it; this is what makes everybody reading a decision agree with the banner.
            //
            // The mapping is deliberately not one-to-one, and the default is the pre-existing
            // behaviour rather than a claim: a name-collision hold has always reported itself here as
            // InsufficientSpace, which is harmless only because the page reads the hold reason and
            // this wait reason has no sentence of its own (QueueWaitDescription). The compatibility
            // holds are separated out because they must NOT reach the advancer's
            // route-back-into-transfer branch, which exists to re-ask the drive about space.
            return QueueAction.Wait(hold switch
            {
                PrintHoldReason.AbrasiveFilamentNeedsHardenedNozzle => QueueWaitReason.IncompatibleWithPrinter,
                PrintHoldReason.IncompatiblePrinterModel => QueueWaitReason.IncompatibleWithPrinter,
                _ => QueueWaitReason.InsufficientSpace,
            });
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
