using System;

using AwesomeAssertions;

using Homespool.Host.Queue;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="QueueRules"/> - what the loop does next, and the one rule with nothing underneath it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The state theory below is the point of this file.</b> Firmware's <c>remote_print_ready</c>
/// accepts a print from <c>Idle</c>, <c>Ready</c>, <c>Stopped</c> <i>and</i> <c>Finished</c>
/// (printer_state.cpp:530-547), so a loop that advanced onto a finished print would be obeyed by the
/// hardware and would print onto the last part. Nothing below this function refuses it - not the
/// printer, and deliberately not the FakePrinter either. So the check is here, and it is checked
/// against every state the enum has rather than the handful anyone thought of.
/// </para>
/// <para>
/// Written against the decision function rather than the hosted service because a rule this sharp
/// should not need a printer, a database or a clock to demonstrate.
/// </para>
/// </remarks>
public class QueueRulesTests
{
    /// <summary>Every state the wire can put a printer in, so the theory below cannot miss one.</summary>
    public static TheoryData<PrinterStatus> AllStates()
    {
        TheoryData<PrinterStatus> data = [];

        foreach (PrinterStatus status in Enum.GetValues<PrinterStatus>())
        {
            data.Add(status);
        }

        return data;
    }

    /// <summary>
    /// <b>A print starts from <c>Ready</c> and from nothing else.</b> Exhaustive over the enum, so a
    /// state added later fails here rather than quietly becoming printable.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStates))]
    public void APrintStartsOnlyFromReady(PrinterStatus status)
    {
        QueueAction action = QueueRules.Decide(Situation(status, arrived: true, path: "/usb/A~1.BGC"));

        if (status == PrinterStatus.Ready)
        {
            action.Kind.Should().Be(QueueActionKind.Print);
        }
        else
        {
            action.Kind.Should().Be(QueueActionKind.Wait,
                "only Ready means a person has offered the printer up for work");
            action.Reason.Should().Be(QueueWaitReason.PrinterNotAvailable);
        }
    }

    /// <summary>
    /// The three states firmware would accept and we will not, named individually - because these are
    /// the ones that read as over-caution and are not.
    /// </summary>
    [Theory]
    [InlineData(PrinterStatus.Finished)]
    [InlineData(PrinterStatus.Stopped)]
    [InlineData(PrinterStatus.Idle)]
    public void TheLoopRefusesTheStatesFirmwareWouldHaveAccepted(PrinterStatus status)
    {
        QueueAction action = QueueRules.Decide(Situation(status, arrived: true, path: "/usb/A~1.BGC"));

        action.Kind.Should().Be(QueueActionKind.Wait,
            "there is a part on the bed in Finished and Stopped, and nobody has offered the printer in Idle");
    }

    /// <summary>
    /// Transfers are <b>not</b> gated on availability - that is pipelining, and it is what removes the
    /// gap between prints.
    /// </summary>
    [Theory]
    [InlineData(PrinterStatus.Printing)]
    [InlineData(PrinterStatus.Paused)]
    [InlineData(PrinterStatus.Attention)]
    [InlineData(PrinterStatus.Ready)]
    public void AFileIsSentWhateverThePrinterIsDoing(PrinterStatus status)
    {
        QueueAction action = QueueRules.Decide(Situation(status, arrived: false, path: null));

        action.Kind.Should().Be(QueueActionKind.Transfer);
    }

    /// <summary>One transfer slot system-wide, so nothing else starts while one runs.</summary>
    [Fact]
    public void NothingIsSentWhileATransferIsAlreadyRunning()
    {
        QueueAction action = QueueRules.Decide(
            Situation(PrinterStatus.Printing, arrived: false, path: null) with { TransferInFlight = true });

        action.Kind.Should().Be(QueueActionKind.Wait);
        action.Reason.Should().Be(QueueWaitReason.Transferring);
    }

    /// <summary>
    /// Arrived but unnamed: the print waits for the <c>FILE_INFO</c> rather than guessing at an 8.3
    /// path, because a wrong guess prints a different file.
    /// </summary>
    [Fact]
    public void APrintWaitsForThePrinterToNameTheFile()
    {
        QueueAction action = QueueRules.Decide(Situation(PrinterStatus.Ready, arrived: true, path: null));

        action.Kind.Should().Be(QueueActionKind.Wait);
        action.Reason.Should().Be(QueueWaitReason.AwaitingPrinterPath);
    }

    /// <summary>A print uses the printer's own path, not the one we transferred to.</summary>
    [Fact]
    public void APrintUsesThePathThePrinterReported()
    {
        QueueAction action = QueueRules.Decide(Situation(PrinterStatus.Ready, arrived: true,
            path: "/usb/CALICA~3.BGC"));

        action.Head!.PrinterPath.Should().Be("/usb/CALICA~3.BGC");
    }

    /// <summary>
    /// A disconnected printer is <c>Nothing</c> rather than <c>Wait</c>: the queue is not stalled, it
    /// simply has nowhere to send anything.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStates))]
    public void ADisconnectedPrinterIsNeverActedOn(PrinterStatus status)
    {
        QueueAction action = QueueRules.Decide(
            Situation(status, arrived: true, path: "/usb/A~1.BGC") with { Connected = false });

        action.Kind.Should().Be(QueueActionKind.Nothing);
    }

    [Fact]
    public void AnEmptyQueueIsNothingToDo()
    {
        QueueAction action = QueueRules.Decide(
            new QueueSnapshot(Connected: true, PrinterStatus.Ready, Head: null, TransferInFlight: false));

        action.Kind.Should().Be(QueueActionKind.Nothing);
    }

    /// <summary>
    /// The theory above is fed from the enum itself rather than a hand-written list, so a state added
    /// later is covered without anyone remembering to add it. This pins that it is not empty and has
    /// not silently stopped enumerating.
    /// </summary>
    [Fact]
    public void TheStateTheoryCoversEveryStatus()
    {
        AllStates().Should().HaveCount(Enum.GetValues<PrinterStatus>().Length).And.NotBeEmpty();
    }

    /// <summary>
    /// A commanded print holds the loop even while the printer still says <c>Ready</c> - the few
    /// seconds between accepting <c>START_PRINT</c> and reporting <c>PRINTING</c>.
    /// </summary>
    /// <remarks>
    /// Measured at 3.1 s on a Core One (<c>START_PRINT</c> +83.7 s, <c>READY -> PRINTING</c> +86.8 s).
    /// Without this the loop sees a ready printer with a queue and commands a second print into the
    /// gap; the partial unique index on the history row would refuse it, but a failed insert is a
    /// worse way to discover it than not doing it.
    /// </remarks>
    [Fact]
    public void APrintAlreadyCommandedStopsAnotherBeingStarted()
    {
        QueueAction action = QueueRules.Decide(
            Situation(PrinterStatus.Ready, arrived: true, path: "/usb/A~1.BGC") with { PrintInFlight = true });

        action.Kind.Should().Be(QueueActionKind.Wait);
        action.Reason.Should().Be(QueueWaitReason.PrintStarting);
    }

    /// <summary>And it holds whatever the printer is reporting, not only while it still says Ready.</summary>
    [Theory]
    [MemberData(nameof(AllStates))]
    public void NothingIsStartedWhileAPrintIsOpen(PrinterStatus status)
    {
        QueueAction action = QueueRules.Decide(
            Situation(status, arrived: true, path: "/usb/A~1.BGC") with { PrintInFlight = true });

        action.Kind.Should().NotBe(QueueActionKind.Print);
    }

    /// <summary>
    /// A transfer is still allowed while a print is open - that is pipelining, and the guard above is
    /// about starting a second <i>print</i>, not about moving the next file.
    /// </summary>
    [Fact]
    public void TheNextFileStillTransfersWhileAPrintIsOpen()
    {
        QueueAction action = QueueRules.Decide(
            Situation(PrinterStatus.Printing, arrived: false, path: null) with { PrintInFlight = true });

        action.Kind.Should().Be(QueueActionKind.Transfer);
    }

    /// <summary>
    /// A blocked file beats the transfer branch, which is the whole reason the block is in the
    /// snapshot at all.
    /// </summary>
    /// <remarks>
    /// The block is enforced in the advancer's transfer path, so the rules used to answer
    /// <c>Transfer</c> for a file that could not fit. Harmless while nothing read the decision - and
    /// the moment a page did, it would have said "sending" directly above a banner saying it could not
    /// be sent.
    /// </remarks>
    [Fact]
    public void ABlockedFileIsNotReportedAsAboutToBeSent()
    {
        QueueAction action = QueueRules.Decide(
            Situation(PrinterStatus.Ready, arrived: false, path: null) with
            {
                BlockedReason = "Not enough space on the printer: needs 4096 bytes, 12 free.",
            });

        action.Kind.Should().Be(QueueActionKind.Wait);
        action.Reason.Should().Be(QueueWaitReason.InsufficientSpace);
    }

    /// <summary>And it holds whatever the printer is doing - the drive is full either way.</summary>
    [Theory]
    [MemberData(nameof(AllStates))]
    public void ABlockedFileHoldsInEveryState(PrinterStatus status)
    {
        QueueAction action = QueueRules.Decide(
            Situation(status, arrived: false, path: null) with { BlockedReason = "no room" });

        action.Kind.Should().Be(QueueActionKind.Wait);
        action.Reason.Should().Be(QueueWaitReason.InsufficientSpace);
    }

    /// <summary>
    /// The page stays quiet where something else already speaks: an active print announces itself, and
    /// the space banner carries its own numbers.
    /// </summary>
    [Theory]
    [InlineData(QueueWaitReason.Transferring, true)]
    [InlineData(QueueWaitReason.AwaitingPrinterPath, true)]
    [InlineData(QueueWaitReason.PrinterNotAvailable, true)]
    [InlineData(QueueWaitReason.InsufficientSpace, false)]
    [InlineData(QueueWaitReason.PrintStarting, false)]
    public void OnlyTheReasonsNothingElseCoversGetASentence(QueueWaitReason reason, bool expected)
    {
        string? sentence = QueueWaitDescription.For(QueueAction.Wait(reason), "benchy.bgcode");

        (sentence is not null).Should().Be(expected);
    }

    /// <summary>A queue that is moving needs no explanation at all.</summary>
    [Fact]
    public void AnActionThatIsNotAWaitSaysNothing()
    {
        QueueWaitDescription.For(QueueAction.Nothing, "benchy.bgcode").Should().BeNull();
        QueueWaitDescription.For(QueueAction.Print(new QueueHead(1, 2, "a.bgcode", true, "/usb/A~1.BGC")), "a.bgcode")
                            .Should().BeNull();
    }

    private static QueueSnapshot Situation(PrinterStatus status, bool arrived, string? path)
    {
        return new QueueSnapshot(
            Connected: true,
            status,
            new QueueHead(QueuedPrintId: 1, PrintFileId: 2, "benchy.bgcode", arrived, path),
            TransferInFlight: false);
    }
}
