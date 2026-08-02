using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Homespool.Host.Services;
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

    private static QueueSnapshot Situation(PrinterStatus status, bool arrived, string? path)
    {
        return new QueueSnapshot(
            Connected: true,
            status,
            new QueueHead(QueuedPrintId: 1, PrintFileId: 2, "benchy.bgcode", arrived, path),
            TransferInFlight: false);
    }
}
