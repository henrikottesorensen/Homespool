using System;

using Homespool.Model;

namespace Homespool.Host.Queue;

/// <summary>
/// Everything <see cref="PrintStartRules.Decide"/> looks at: what the printer is saying about itself,
/// what it said when asked about its job, and how long ago we asked it to print.
/// </summary>
/// <param name="Connected">Whether the printer has a live connection right now.</param>
/// <param name="Status">Its last-known state, from <c>PrinterLiveState</c>.</param>
/// <param name="ReportedSinceCommand">
/// Whether telemetry has arrived since the <c>START_PRINT</c> went out.
/// <para>
/// <b>Without this, a stale row would answer for the printer.</b> A printer that fell off the
/// network the instant it was commanded keeps its last live state on file, and reading that as "it
/// is idle, so it never started" is the same mistake the timeout was: treating an absence of news
/// as news.
/// </para>
/// </param>
/// <param name="SinceCommanded">How long ago the command was sent.</param>
/// <param name="Answer">What asking about the job established, if anything.</param>
public sealed record PrintStartObservation(
    bool Connected,
    PrinterStatus Status,
    bool ReportedSinceCommand,
    TimeSpan SinceCommanded,
    JobAnswer Answer);
