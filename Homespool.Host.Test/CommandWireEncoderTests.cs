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

    /// <summary>
    /// <c>SET_TOKEN</c> carries its token as a <b>string in kwargs</b>, pinned against firmware's own
    /// parser test (<c>tests/unit/connect/command.cpp:156</c>), which accepts exactly
    /// <c>{"command": "SET_TOKEN","kwargs": {"token":"toktoktok"}}</c>.
    /// </summary>
    /// <remarks>
    /// Its sibling tests at <c>:161</c> and <c>:165</c> prove firmware answers <c>BrokenCommand</c>
    /// for a missing token and for one longer than <see cref="SetToken.MaxTokenLength"/>, so the key
    /// name and the type are both load-bearing. The class carried <c>byte[]</c> while it was an unsent
    /// marker; that would have serialised as base64 and been refused on arrival, which no test could
    /// have caught while nothing sent it.
    /// </remarks>
    [Fact]
    public void EncodeWritesSetTokensTokenAsAStringInKwargs()
    {
        // Act
        byte[] frame = CommandWireEncoder.Encode(9, new SetToken { Token = "toktoktok" });
        using JsonDocument payload = JsonDocument.Parse(frame.AsSpan(9).ToArray());

        // Assert
        payload.RootElement.GetProperty("command").GetString().Should().Be("SET_TOKEN");

        JsonElement token = payload.RootElement.GetProperty("kwargs").GetProperty("token");

        token.ValueKind.Should().Be(JsonValueKind.String, "firmware matches is_arg(\"token\", Type::String)");
        token.GetString().Should().Be("toktoktok");
    }

    /// <summary>
    /// The argument-bearing shape, pinned against firmware's own parser test
    /// (tests/unit/connect/command.cpp:141), which accepts
    /// <c>{"command": "START_INLINE_DOWNLOAD", "args": [], "kwargs": {...}}</c>. The four kwargs are
    /// <c>ARGS_INLINE_DOWN</c> (command.cpp:89); their sibling tests at :149-151 prove firmware
    /// rejects the command outright when any is absent, so this asserts all four.
    /// </summary>
    [Fact]
    public void EncodeWritesArgsAndKwargsForACommandThatHasArguments()
    {
        // Arrange
        StartConnectDownload commandData = new()
        {
            Path = "/usb/whatever.gcode",
            Hash = "abcdef",
            TeamId = 42,
            OriginalSize = 1024,
        };

        // Act
        byte[] frame = CommandWireEncoder.Encode(7, commandData);
        using JsonDocument payload = JsonDocument.Parse(frame.AsSpan(9).ToArray());

        // Assert
        JsonElement root = payload.RootElement;
        root.GetProperty("command").GetString().Should().Be("START_CONNECT_DOWNLOAD");
        root.GetProperty("args").EnumerateArray().Should().BeEmpty();

        JsonElement kwargs = root.GetProperty("kwargs");
        kwargs.GetProperty("path").GetString().Should().Be("/usb/whatever.gcode");
        kwargs.GetProperty("team_id").GetUInt64().Should().Be(42);
        kwargs.GetProperty("hash").GetString().Should().Be("abcdef");
        kwargs.GetProperty("orig_size").GetInt64().Should().Be(1024);

        // Numbers, not strings: firmware parses each kwarg into a fixed C type and rejects the whole
        // command on a mismatch rather than coercing.
        kwargs.GetProperty("team_id").ValueKind.Should().Be(JsonValueKind.Number);
        kwargs.GetProperty("orig_size").ValueKind.Should().Be(JsonValueKind.Number);
    }

    /// <summary>
    /// The NO_ARGS shape is the one verified against the live MK3.5, so adding the kwargs path must
    /// not have widened it - an <c>"args": []</c> appearing on <c>PAUSE_PRINT</c> would be a silent
    /// change to a hardware-proven frame.
    /// </summary>
    [Fact]
    public void EncodeLeavesArgumentlessCommandsWithNoArgsKey()
    {
        // Act
        byte[] frame = CommandWireEncoder.Encode(7, new PausePrint());
        using JsonDocument payload = JsonDocument.Parse(frame.AsSpan(9).ToArray());

        // Assert
        payload.RootElement.TryGetProperty("args", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("kwargs", out _).Should().BeFalse();
    }
}
