using System;

using Homespool.Model;

namespace Homespool.Host.Queue;

/// <summary>
/// Decides what a <c>START_PRINT</c> that went unanswered actually did, from what the printer has
/// said since.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule this exists to enforce: a timeout is not a negative answer.</b> A physical,
/// non-idempotent command that does not acknowledge means <i>unknown</i>, and unknown is resolved by
/// observing the machine - never by classifying the failure. For <c>START_PRINT</c> the point is
/// sharper than it sounds, because the two are correlated rather than independent: the printer is
/// slow to answer <b>because</b> it accepted the command and went off to home and heat, so the
/// timeout is caused by the success it was read as ruling out (observed on hardware, 2026-08-21).
/// </para>
/// <para>
/// <b>Pure and separate from the advancer, for the same reason <see cref="QueueRules"/> is.</b> Each
/// of the three verdicts that is not <see cref="PrintStartVerdict.KeepWaiting"/> throws something
/// away - a print, a queue entry, or the queue's own motion - and being wrong about any of them is
/// expensive and quiet. A decision function with no I/O can be tested over every combination of what
/// the printer might be saying, which is the only way rules like these are held down.
/// </para>
/// <para>
/// <b>It decides what happened, never what to do about it.</b> Opening, promoting and removing rows
/// belongs to the caller holding the database.
/// </para>
/// </remarks>
public static class PrintStartRules
{
    /// <summary>
    /// The states a printer reports while it has a print in hand - the ones that are not evidence a
    /// command was ignored.
    /// </summary>
    /// <remarks>
    /// <c>Paused</c> and <c>Attention</c> are stalls inside a print rather than endings, so they
    /// belong here beside <c>Printing</c>. <c>Finished</c> and <c>Stopped</c> deliberately do not:
    /// they mean a print <i>ended</i>, and one that ended inside this window is either not ours or
    /// something no rule here should be guessing about.
    /// </remarks>
    public static bool LooksBusy(PrinterStatus status)
    {
        return status is PrinterStatus.Printing or PrinterStatus.Paused or PrinterStatus.Attention;
    }

    /// <summary>Works out what became of a print we commanded and never heard back about.</summary>
    /// <param name="observation">What the printer has said since.</param>
    /// <param name="grace">
    /// How long a printer may report itself not-printing before that means the command was ignored.
    /// A printer legitimately keeps saying <c>READY</c> for a few seconds after accepting one.
    /// </param>
    /// <param name="giveUpAfter">
    /// How long to keep asking a connected printer that will not answer, before holding the queue
    /// instead.
    /// </param>
    public static PrintStartVerdict Decide(PrintStartObservation observation, TimeSpan grace, TimeSpan giveUpAfter)
    {
        ArgumentNullException.ThrowIfNull(observation);

        // The printer's own words about its own job, which outrank every inference below. Note that
        // all three arms are reached only by having asked and been answered, so none of them can be
        // produced by a printer that has merely gone quiet.
        switch (observation.Answer)
        {
            case JobAnswer.Ours:
                return PrintStartVerdict.Started;

            // Whatever is running, our command did not start it - somebody printed from the panel,
            // or the machine has nothing at all. The entry stays queued and waits its turn.
            case JobAnswer.SomebodyElses:
            case JobAnswer.NoJob:
                return PrintStartVerdict.NeverStarted;

            default:
                break;
        }

        if (!observation.Connected)
        {
            // Nothing can be established about a printer we cannot reach, and the question keeps:
            // the print, if there is one, is still running and will still be describable when it
            // comes back.
            return PrintStartVerdict.KeepWaiting;
        }

        // A printer reporting no job at all, freshly, and for longer than it could plausibly still
        // be getting started. This is the ordinary negative - the command was written to a socket
        // and never acted on - and it is deliberately the only inference here that is allowed to
        // conclude anything, because it rests on the printer having spoken since we asked.
        if (observation.Answer == JobAnswer.NotAsked
            && observation.ReportedSinceCommand
            && !LooksBusy(observation.Status)
            && observation.SinceCommanded >= grace)
        {
            return PrintStartVerdict.NeverStarted;
        }

        // Connected, reporting a job, and refusing to describe it - for long enough that it is not
        // about to start. Advancing might print the file twice and failing would throw away a print
        // that may never have run, so the queue stops and a person decides.
        return observation.SinceCommanded >= giveUpAfter ?
            PrintStartVerdict.Unresolvable :
            PrintStartVerdict.KeepWaiting;
    }
}
