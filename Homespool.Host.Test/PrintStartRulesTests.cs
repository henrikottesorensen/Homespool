using System;

using AwesomeAssertions;

using Homespool.Host.Queue;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrintStartRules"/> - what became of a <c>START_PRINT</c> that was never acknowledged.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these rules exist for was seen on hardware on 2026-08-21</b>, and it is worth
/// stating here because every case below is an angle on it. A file was queued, transferred and
/// commanded; the printer accepted the command, began homing and heating, and did not answer inside
/// the ten-second command timeout. The queue recorded that as "the print did not happen", kept the
/// entry, and stood ready to print the same file again the moment somebody made the printer ready.
/// </para>
/// <para>
/// <b>The trap is that the timeout was <i>caused by</i> the success.</b> The printer was slow to
/// acknowledge because it had accepted the command and gone off to do the physical work - so the two
/// are correlated, and the guess that reads best in the abstract ("no answer, so it did not happen")
/// is the one that is wrong most often in practice.
/// </para>
/// <para>
/// So the rules resolve by observation, and three of the four verdicts throw something away: a
/// print, a queue entry, or the queue's own motion. <b>Anything that is not evidence must produce
/// <see cref="PrintStartVerdict.KeepWaiting"/></b>, and that is what most of this file checks.
/// </para>
/// </remarks>
public class PrintStartRulesTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(15);

    /// <summary>Every state the wire can put a printer in, so the theories cannot miss one.</summary>
    public static TheoryData<PrinterStatus> AllStates()
    {
        TheoryData<PrinterStatus> data = [];

        foreach (PrinterStatus status in Enum.GetValues<PrinterStatus>())
        {
            data.Add(status);
        }

        return data;
    }

    /// <summary>Every answer the printer can give, likewise.</summary>
    public static TheoryData<JobAnswer> AllAnswers()
    {
        TheoryData<JobAnswer> data = [];

        foreach (JobAnswer answer in Enum.GetValues<JobAnswer>())
        {
            data.Add(answer);
        }

        return data;
    }

    /// <summary>
    /// <b>The printer naming our file is the only thing that adopts a print</b>, and it does so
    /// whatever else is true.
    /// </summary>
    /// <remarks>
    /// Exhaustive over the printer's reported state on purpose. The status is what the queue was
    /// reading before, and it is the wrong instrument: it can say a printer is printing, never
    /// whose print it is.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllStates))]
    public void NamingOurFileIsWhatAdoptsAPrint(PrinterStatus status)
    {
        PrintStartVerdict verdict = Decide(Seen(status, JobAnswer.Ours));

        verdict.Should().Be(PrintStartVerdict.Started);
    }

    /// <summary>
    /// A print that turns out to be somebody else's means the command was not acted on, and it means
    /// it immediately.
    /// </summary>
    /// <remarks>
    /// <b>The one definite negative anywhere on this path</b> - a <i>name</i> was compared to reach
    /// it, which is the thing the start window cannot fake. It does not wait out the grace: an
    /// answer that names a different file needs no corroboration from a clock. <c>NoJob</c> used to
    /// sit beside it here, and that was a defect - see the start-window tests below.
    /// </remarks>
    [Fact]
    public void SomebodyElsesFileSettlesItImmediately()
    {
        PrintStartVerdict verdict = Decide(Seen(PrinterStatus.Printing, JobAnswer.SomebodyElses) with
        {
            SinceCommanded = TimeSpan.FromSeconds(1),
        });

        verdict.Should().Be(PrintStartVerdict.NeverStarted);
    }

    /// <summary>
    /// <b>"No job in progress" inside the grace is the start window, not an answer.</b>
    /// </summary>
    /// <remarks>
    /// Firmware renders that answer against its momentary state, and a print it has accepted passes
    /// through a state that reports <c>READY</c> with no job before it reports <c>PRINTING</c> - so
    /// moments after a command, this is what a print that is <i>starting</i> sounds like. Concluding
    /// from it deletes the record of a print that is running, which is a phantom minted by the
    /// resolution itself.
    /// </remarks>
    [Theory]
    [InlineData(PrinterStatus.Ready)]
    [InlineData(PrinterStatus.Idle)]
    [InlineData(PrinterStatus.Printing)]
    public void NoJobInsideTheGraceIsTheStartWindowNotAnAnswer(PrinterStatus status)
    {
        PrintStartVerdict verdict = Decide(Seen(status, JobAnswer.NoJob) with
        {
            SinceCommanded = TimeSpan.FromSeconds(3),
        });

        verdict.Should().Be(PrintStartVerdict.KeepWaiting);
    }

    /// <summary>
    /// The same answer, past the grace, from a printer with nothing in hand: now it concludes. The
    /// start window is seconds; a minute later it is the machine stating there is nothing running.
    /// </summary>
    [Fact]
    public void NoJobPastTheGraceOnAnIdleHandedPrinterMeansItNeverStarted()
    {
        PrintStartVerdict verdict = Decide(Seen(PrinterStatus.Ready, JobAnswer.NoJob));

        verdict.Should().Be(PrintStartVerdict.NeverStarted);
    }

    /// <summary>
    /// A printer that reports a job in telemetry while answering that it has none is contradicting
    /// itself - that never concludes, and is eventually given up on like any other refusal to
    /// describe.
    /// </summary>
    [Fact]
    public void NoJobFromAPrinterThatLooksBusyNeverConcludes()
    {
        PrintStartObservation seen = Seen(PrinterStatus.Printing, JobAnswer.NoJob);

        Decide(seen).Should().Be(PrintStartVerdict.KeepWaiting);

        Decide(seen with { SinceCommanded = TimeSpan.FromMinutes(16) })
            .Should().Be(PrintStartVerdict.Unresolvable);
    }

    /// <summary>
    /// A printer still reporting <c>READY</c> moments after being commanded has not refused
    /// anything - it is warming up.
    /// </summary>
    /// <remarks>
    /// <b>The window is real and it is hardware-specific.</b> A Core One keeps saying <c>READY</c>
    /// for 3.1 s after accepting a print while it works through preview-init and heating; an MK3.5
    /// says <c>PRINTING</c> in the very first sample. Concluding from a not-printing status inside
    /// that gap would drop a print that was starting perfectly well - and the fake could never show
    /// it, because the fake transitions instantly.
    /// </remarks>
    [Fact]
    public void AReadyPrinterInsideTheGraceProvesNothing()
    {
        PrintStartVerdict verdict = Decide(Seen(PrinterStatus.Ready, JobAnswer.NotAsked) with
        {
            SinceCommanded = TimeSpan.FromSeconds(3),
        });

        verdict.Should().Be(PrintStartVerdict.KeepWaiting);
    }

    /// <summary>
    /// A printer that has spoken since, reports no job, and has had long enough: the command was
    /// never acted on.
    /// </summary>
    [Fact]
    public void AFreshlySilentPrinterPastTheGraceMeansItNeverStarted()
    {
        PrintStartVerdict verdict = Decide(Seen(PrinterStatus.Ready, JobAnswer.NotAsked));

        verdict.Should().Be(PrintStartVerdict.NeverStarted);
    }

    /// <summary>
    /// <b>The same, from a printer that has not said anything since - and it settles nothing.</b>
    /// </summary>
    /// <remarks>
    /// This is the whole defect in miniature, one layer down. A printer that fell off the network the
    /// instant it was commanded leaves its last live state on file, and reading that as "it is idle,
    /// so it never started" is again treating an absence of news as news. The print may well be
    /// running.
    /// </remarks>
    [Fact]
    public void AStaleLiveStateIsNotAnAnswer()
    {
        PrintStartVerdict verdict = Decide(Seen(PrinterStatus.Ready, JobAnswer.NotAsked) with
        {
            ReportedSinceCommand = false,
        });

        verdict.Should().Be(PrintStartVerdict.KeepWaiting);
    }

    /// <summary>
    /// A printer with a print in hand is never read as having ignored the command, however long it
    /// has been.
    /// </summary>
    /// <remarks>
    /// <c>Paused</c> and <c>Attention</c> sit here beside <c>Printing</c> because they are stalls
    /// <i>inside</i> a print rather than endings - a filament runout mid-print is the archetype - so
    /// a machine in one of them is a machine that took something.
    /// </remarks>
    [Theory]
    [InlineData(PrinterStatus.Printing)]
    [InlineData(PrinterStatus.Paused)]
    [InlineData(PrinterStatus.Attention)]
    public void APrinterWithAPrintInHandIsNeverReadAsHavingIgnoredUs(PrinterStatus status)
    {
        PrintStartVerdict verdict = Decide(Seen(status, JobAnswer.NotAsked));

        verdict.Should().NotBe(PrintStartVerdict.NeverStarted);
    }

    /// <summary>
    /// A printer we cannot reach settles nothing at all, whatever it last said and however long ago.
    /// </summary>
    /// <remarks>
    /// And the question keeps: a print that is running will still be running, and still describable,
    /// when the printer comes back. Waiting costs a tick; guessing costs a duplicate print or a
    /// dropped one.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllStates))]
    public void ADisconnectedPrinterSettlesNothing(PrinterStatus status)
    {
        PrintStartVerdict verdict = Decide(Seen(status, JobAnswer.NotAsked) with
        {
            Connected = false,
            SinceCommanded = TimeSpan.FromHours(3),
        });

        verdict.Should().Be(PrintStartVerdict.KeepWaiting);
    }

    /// <summary>
    /// A connected printer reporting a job it will not describe is eventually given up on - and the
    /// verdict says so rather than pretending to know.
    /// </summary>
    /// <remarks>
    /// <b>There is a bound at all because waiting for days on a connected printer is not an answer</b>
    /// (Henrik, 2026-08-22). What the bound must not do is guess, which is why this is its own
    /// verdict: the caller holds the queue and asks a person, rather than advancing onto a print that
    /// may already have run.
    /// </remarks>
    [Fact]
    public void APrinterThatWillNotDescribeItsJobIsEventuallyGivenUpOn()
    {
        PrintStartObservation seen = Seen(PrinterStatus.Printing, JobAnswer.Inconclusive);

        Decide(seen with { SinceCommanded = TimeSpan.FromMinutes(14) })
            .Should().Be(PrintStartVerdict.KeepWaiting, "it is still worth another ask");

        Decide(seen with { SinceCommanded = TimeSpan.FromMinutes(16) })
            .Should().Be(PrintStartVerdict.Unresolvable);
    }

    /// <summary>
    /// <b>An unreadable answer never decides anything.</b>
    /// </summary>
    /// <remarks>
    /// <see cref="JobAnswer.Inconclusive"/> covers a question that went unanswered, a job the printer
    /// only remembers - a <c>FIN_OK</c> carrying no file name - and a refusal worded in a way nobody
    /// has read yet. Letting any of those conclude would be the original defect returning by the door
    /// it came in.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllStates))]
    public void AnInconclusiveAnswerNeverConcludes(PrinterStatus status)
    {
        PrintStartVerdict verdict = Decide(Seen(status, JobAnswer.Inconclusive));

        verdict.Should().BeOneOf(PrintStartVerdict.KeepWaiting, PrintStartVerdict.Unresolvable);
    }

    /// <summary>
    /// <b>Nothing about a printer that has gone quiet ever produces a print.</b> Exhaustive over
    /// every state and every answer.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="NamingOurFileIsWhatAdoptsAPrint"/>, and the sharper half: adopting a
    /// print deletes somebody's queue entry, so it must rest on the printer having named the file and
    /// on nothing else. A state, a clock and a connection between them must never add up to a
    /// <see cref="PrintStartVerdict.Started"/>.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllAnswers))]
    public void OnlyTheAnswerCanAdopt(JobAnswer answer)
    {
        foreach (PrinterStatus status in Enum.GetValues<PrinterStatus>())
        {
            foreach (bool connected in new[] { true, false })
            {
                foreach (bool reported in new[] { true, false })
                {
                    foreach (TimeSpan elapsed in new[]
                             {
                                 TimeSpan.Zero, TimeSpan.FromSeconds(90), TimeSpan.FromHours(1),
                             })
                    {
                        PrintStartVerdict verdict = Decide(new PrintStartObservation(connected, status, reported,
                                                                                     elapsed, answer));

                        if (answer == JobAnswer.Ours)
                        {
                            verdict.Should().Be(PrintStartVerdict.Started);
                        }
                        else
                        {
                            verdict.Should().NotBe(PrintStartVerdict.Started,
                                                   $"{status}/{connected}/{reported}/{elapsed} is not the printer naming our file");
                        }
                    }
                }
            }
        }
    }

    private static PrintStartVerdict Decide(PrintStartObservation observation)
    {
        return PrintStartRules.Decide(observation, Grace, GiveUp);
    }

    /// <summary>
    /// The ordinary case to vary from: connected, reporting freshly, and well past the grace - so
    /// every test below says in its own name what makes it different.
    /// </summary>
    private static PrintStartObservation Seen(PrinterStatus status, JobAnswer answer)
    {
        return new PrintStartObservation(Connected: true,
                                         status,
                                         ReportedSinceCommand: true,
                                         SinceCommanded: TimeSpan.FromSeconds(90),
                                         answer);
    }
}
