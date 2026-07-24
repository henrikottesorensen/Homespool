using System.Threading.Tasks;

using AwesomeAssertions;

using PrinterService.Host.PrusaConnect;
using PrinterService.Model;

namespace PrinterService.Host.Test;

/// <summary>
/// <see cref="PrinterCommandCorrelator"/> - one pending command per printer, matching the firmware's
/// own single-in-flight-command limit (connect.cpp:469-476 at the pinned ref).
/// </summary>
public class PrinterCommandCorrelatorTests
{
    [Fact]
    public void TryBeginCommandRejectsASecondBeginForTheSamePrinterWhileOneIsPending()
    {
        // Arrange
        PrinterCommandCorrelator correlator = new();
        correlator.TryBeginCommand(printerId: 1, commandId: 10, out _).Should().BeTrue();

        // Act
        bool began = correlator.TryBeginCommand(printerId: 1, commandId: 11, out _);

        // Assert
        began.Should().BeFalse();
    }

    [Fact]
    public void TryBeginCommandAcceptsAConcurrentBeginForADifferentPrinter()
    {
        // Arrange
        PrinterCommandCorrelator correlator = new();
        correlator.TryBeginCommand(printerId: 1, commandId: 10, out _).Should().BeTrue();

        // Act
        bool began = correlator.TryBeginCommand(printerId: 2, commandId: 20, out _);

        // Assert
        began.Should().BeTrue();
    }

    [Fact]
    public async Task ObserveEventWithMatchingCommandIdCompletesTheOutcome()
    {
        // Arrange
        PrinterCommandCorrelator correlator = new();
        correlator.TryBeginCommand(printerId: 1, commandId: 10, out Task<CommandOutcome> outcome);

        // Act
        correlator.ObserveEvent(printerId: 1, commandId: 10, Events.Rejected, "No print to pause");

        // Assert
        CommandOutcome result = await outcome;
        result.EventType.Should().Be(Events.Rejected);
        result.Reason.Should().Be("No print to pause");
    }

    [Fact]
    public void ObserveEventWithNonMatchingCommandIdLeavesTheOutcomePending()
    {
        // Arrange
        PrinterCommandCorrelator correlator = new();
        correlator.TryBeginCommand(printerId: 1, commandId: 10, out Task<CommandOutcome> outcome);

        // Act
        correlator.ObserveEvent(printerId: 1, commandId: 99, Events.Finished, null);

        // Assert
        outcome.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void CancelCancelsThePendingTask()
    {
        // Arrange
        PrinterCommandCorrelator correlator = new();
        correlator.TryBeginCommand(printerId: 1, commandId: 10, out Task<CommandOutcome> outcome);

        // Act
        correlator.Cancel(printerId: 1);

        // Assert
        outcome.IsCanceled.Should().BeTrue();
    }

    [Fact]
    public void AfterCompletionANewCommandCanBeginForTheSamePrinter()
    {
        // Arrange
        PrinterCommandCorrelator correlator = new();
        correlator.TryBeginCommand(printerId: 1, commandId: 10, out _);
        correlator.ObserveEvent(printerId: 1, commandId: 10, Events.Finished, null);

        // Act
        bool began = correlator.TryBeginCommand(printerId: 1, commandId: 11, out _);

        // Assert
        began.Should().BeTrue();
    }
}
