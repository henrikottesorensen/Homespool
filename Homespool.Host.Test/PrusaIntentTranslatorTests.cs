using System;
using System.Collections.Generic;

using AwesomeAssertions;

using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrusaIntentTranslator"/> - the written-out table from domain intents to Prusa
/// Connect commands. The table is asserted pair by pair because its whole reason to exist is that
/// the correspondence must never be assumed mechanical.
/// </summary>
public sealed class PrusaIntentTranslatorTests
{
    public static TheoryData<IPrinterIntent, string> Vocabulary => new()
    {
        { new Printing.StartPrint("/usb/FILE.BGC"), "START_PRINT" },
        { new Printing.StopPrint(), "STOP_PRINT" },
        { new Printing.PausePrint(), "PAUSE_PRINT" },
        { new Printing.ResumePrint(), "RESUME_PRINT" },
        { new Printing.SetPrinterReady(), "SET_PRINTER_READY" },
        { new Printing.CancelPrinterReady(), "CANCEL_PRINTER_READY" },
        { new Printing.SetPrinterIdle(), "SET_IDLE" },
        { new Printing.SetTemperatures(215, 60), "GCODE" },
    };

    [Theory]
    [MemberData(nameof(Vocabulary))]
    public void EveryIntentTranslatesToItsWireCommand(IPrinterIntent intent, string expectedWireName)
    {
        PrusaConnect.Commands.ISendableCommand command = PrusaIntentTranslator.ToCommand(intent);

        command.WireName.Should().Be(expectedWireName);
    }

    [Fact]
    public void StartPrintCarriesItsPath()
    {
        PrusaConnect.Commands.ISendableCommand command =
            PrusaIntentTranslator.ToCommand(new Printing.StartPrint("/usb/SHAPE-~2.BGC"));

        IReadOnlyDictionary<string, object?>? arguments = command.Arguments;

        arguments.Should().NotBeNull();
        arguments["path"].Should().Be("/usb/SHAPE-~2.BGC");
    }

    [Fact]
    public void SetTemperaturesCarriesBothTargets()
    {
        PrusaConnect.Commands.ISendableCommand command =
            PrusaIntentTranslator.ToCommand(new Printing.SetTemperatures(215, 60));

        PrusaConnect.Commands.SetTemperatures gcode = command.Should()
                                                             .BeOfType<PrusaConnect.Commands.SetTemperatures>().Subject;

        gcode.NozzleTemperature.Should().Be(215);
        gcode.BedTemperature.Should().Be(60);
    }

    [Fact]
    public void AnIntentWithoutACommandThrowsRatherThanBeingDropped()
    {
        Action act = () => PrusaIntentTranslator.ToCommand(new UnmappedIntent());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AnIntentsNameIsItsTypeNameNotAWireWord()
    {
        // Through the interface: Name is a default interface member, which is invisible on the
        // concrete record - exactly how the controller and pages read it.
        IPrinterIntent intent = new Printing.StopPrint();

        intent.Name.Should().Be("StopPrint");
    }

    private sealed record UnmappedIntent : IPrinterIntent;
}
