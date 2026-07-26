using System;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="CommandWireEncoder"/> - the 9-byte-header-plus-JSON frame the firmware expects
/// (Prusa-Firmware-Buddy connect.cpp:357-557, command.cpp:149-166 at the pinned ref).
/// </summary>
public class CommandWireEncoderTests
{
    [Fact]
    public void EncodeStartsWithTheJTypeByte()
    {
        // Act
        byte[] frame = CommandWireEncoder.Encode(1, new PausePrint());

        // Assert
        // Only J (JSON command) is modeled by ISendableCommand this pass - G/F/D/T are out of scope.
        frame[0].Should().Be((byte)'J');
    }

    [Fact]
    public void EncodeWritesTheCommandIdAsEightUppercaseHexDigits()
    {
        // Act
        byte[] frame = CommandWireEncoder.Encode(0x1A, new PausePrint());

        // Assert
        Encoding.ASCII.GetString(frame, 1, 8).Should().Be("0000001A");
    }

    [Fact]
    public void EncodeRoundTripsALargeCommandId()
    {
        // Act
        byte[] frame = CommandWireEncoder.Encode(0xDEADBEEF, new PausePrint());

        // Assert
        Encoding.ASCII.GetString(frame, 1, 8).Should().Be("DEADBEEF");
    }

    [Theory]
    [InlineData(typeof(PausePrint), "PAUSE_PRINT")]
    [InlineData(typeof(ResumePrint), "RESUME_PRINT")]
    [InlineData(typeof(StopPrint), "STOP_PRINT")]
    [InlineData(typeof(SetPrinterReady), "SET_PRINTER_READY")]
    [InlineData(typeof(CancelPrinterReady), "CANCEL_PRINTER_READY")]
    [InlineData(typeof(SetPrinterIdle), "SET_IDLE")]
    public void EncodePayloadIsExactlyTheCommandFieldWithNoArgsOrKwargs(System.Type commandType, string expectedWireName)
    {
        // Arrange
        ISendableCommand commandData = (ISendableCommand)System.Activator.CreateInstance(commandType)!;

        // Act
        byte[] frame = CommandWireEncoder.Encode(7, commandData);
        using JsonDocument payload = JsonDocument.Parse(frame.AsSpan(9).ToArray());

        // Assert
        // Confirmed against command.cpp:149-166: a NO_ARGS command's JSON body is just
        // {"command": "..."} - no "args"/"kwargs" wrapper.
        payload.RootElement.EnumerateObject().Should().ContainSingle();
        payload.RootElement.GetProperty("command").GetString().Should().Be(expectedWireName);
    }
}
