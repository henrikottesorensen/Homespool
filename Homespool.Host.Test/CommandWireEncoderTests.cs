using System;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;
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
        frame[0].Should().Be((byte)'J');
    }

    // ---------- the gcode frame ----------

    /// <summary>
    /// A gcode command is a <c>G</c> frame: the same nine-byte header, and the line itself as the
    /// body rather than a JSON document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserted byte for byte because nothing else checks it.</b> The J shape was confirmed
    /// against the live MK3.5; this one is read from
    /// <c>connect.cpp:469-508</c> and has never been sent to a printer. A malformed header would be
    /// refused by firmware as an unknown frame type and surface as a command that silently does
    /// nothing, so the test is the only thing standing in for hardware until it is run against some.
    /// </para>
    /// <para>
    /// <c>G</c> rather than <c>F</c>: firmware understands both, <c>F</c> being "forced" and meant to
    /// be the one accepted mid-print. It does not implement the distinction - so this asserts the
    /// intent, not a behaviour the printer currently honours.
    /// </para>
    /// </remarks>
    [Fact]
    public void AGcodeCommandIsAGFrameCarryingTheLineItself()
    {
        // Act
        byte[] frame = CommandWireEncoder.Encode(0x2B, new SetNozzleTemperature(215));

        // Assert
        frame[0].Should().Be((byte)'G', "a gcode frame is not a JSON one");
        Encoding.ASCII.GetString(frame, 1, 8).Should().Be("0000002B");
        Encoding.ASCII.GetString(frame, 9, frame.Length - 9).Should().Be("M104 S215");
        frame.Length.Should().Be(9 + "M104 S215".Length, "there is no terminator and no JSON wrapper");
    }

    /// <summary>The bed's command is the same shape, with the code firmware uses for the heatbed.</summary>
    [Fact]
    public void TheBedCommandCarriesM140()
    {
        byte[] frame = CommandWireEncoder.Encode(1, new SetBedTemperature(60));

        Encoding.ASCII.GetString(frame, 9, frame.Length - 9).Should().Be("M140 S60");
    }

    /// <summary>Zero is off, and has to survive as a literal <c>S0</c> rather than being elided.</summary>
    [Fact]
    public void CoolingDownSendsAnExplicitZero()
    {
        byte[] frame = CommandWireEncoder.Encode(1, new SetNozzleTemperature(0));

        Encoding.ASCII.GetString(frame, 9, frame.Length - 9).Should().Be("M104 S0");
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
    /// for a missing token and for one longer than
    /// <see cref="PrusaConnectConstants.PrinterTokenLength"/>, so the key
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

    /// <summary>
    /// <c>SEND_FILE_INFO</c> carries the path it asks about, and nothing else - the one kwarg
    /// firmware reads (planner.cpp:751-759). A directory path is what makes it a listing.
    /// </summary>
    [Fact]
    public void EncodeWritesSendFileInfosPathAsItsOnlyKwarg()
    {
        // Act
        byte[] frame = CommandWireEncoder.Encode(7, new SendFileInfo { Path = "/usb" });
        using JsonDocument payload = JsonDocument.Parse(frame.AsSpan(9).ToArray());

        // Assert
        JsonElement root = payload.RootElement;
        root.GetProperty("command").GetString().Should().Be("SEND_FILE_INFO");

        JsonElement kwargs = root.GetProperty("kwargs");
        kwargs.GetProperty("path").GetString().Should().Be("/usb");
        kwargs.EnumerateObject().Should().ContainSingle();
    }

    // ---------- escaping, which firmware only half decodes ----------

    /// <summary>
    /// A path keeps its accents, its <c>&amp;</c> and its <c>+</c> as literal UTF-8 on the wire, with
    /// no backslash anywhere in the frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The assertion is on the bytes, and it has to be.</b> Parsing the payload back with
    /// <see cref="JsonDocument"/> and reading <c>path</c> returns the same string either way - .NET
    /// decodes <c>\u00E9</c> perfectly well, so a test written that way passes against the bug it is
    /// meant to catch. The traffic log is no help either: it re-serialises each command rather than
    /// recording the frame, so it cannot show which escapes went out.
    /// </para>
    /// <para>
    /// <b>Why a backslash is the thing to assert on.</b> Firmware's <c>unescape_json_i</c>
    /// (<c>json_encode.cpp:21-29</c>) decodes only <c>\b \f \n \r \t \" \\</c>, so a <c>\uXXXX</c>
    /// keeps its backslash - and FatFs treats a backslash as a path separator (<c>ff.c:51</c>), which
    /// turns one filename into several path segments and fails the <c>mkdir</c> that
    /// <c>Transfer::begin</c> does to hold a resumable transfer. Measured on a Core One at
    /// <c>6.8.1+16182</c>: this exact name was refused with "Failed to create directory", ~1 500 times.
    /// </para>
    /// </remarks>
    [Fact]
    public void EncodeLeavesAPathsSpecialCharactersLiteralOnTheWire()
    {
        // Arrange
        StartConnectDownload commandData = new()
        {
            Path = "/usb/Le téstè & stüff+1.bgcode",
            Hash = "abcdef",
            TeamId = 1,
            OriginalSize = 494464,
        };

        // Act
        byte[] frame = CommandWireEncoder.Encode(7, commandData);
        string payload = Encoding.UTF8.GetString(frame, 9, frame.Length - 9);

        // Assert
        payload.Should().Contain("\"path\":\"/usb/Le téstè & stüff+1.bgcode\"");
        payload.Should().NotContain("\\",
                                    "firmware keeps the backslash of any escape it cannot decode, and FatFs splits the path on it");
    }

    /// <summary>
    /// The same command for a peer that <i>does</i> decode <c>\uXXXX</c> takes the stock encoding -
    /// the toggle that lets a fixed firmware stop using the workaround.
    /// </summary>
    /// <remarks>
    /// Nothing sets <see cref="PrinterDialect.DecodesJsonUnicodeEscapes"/> today, because no released
    /// firmware decodes those escapes; the flag is per-connection so that one can, without an
    /// appliance-wide setting having to describe a fleet where only some printers are patched. Both
    /// encodings are valid JSON and parse to the same string, which is why the assertion is again on
    /// the bytes rather than on the parsed value.
    /// </remarks>
    [Fact]
    public void EncodeUsesTheStockEscapingForAPeerThatDecodesEscapes()
    {
        // Arrange
        PrinterDialect patched = PrinterDialect.BuddySocket with { DecodesJsonUnicodeEscapes = true };
        SendFileInfo commandData = new() { Path = "/usb/Le téstè & stüff.bgcode" };

        // Act
        byte[] frame = CommandWireEncoder.Encode(7, commandData, patched);
        string payload = Encoding.UTF8.GetString(frame, 9, frame.Length - 9);

        // Assert
        payload.Should().Contain(@"\u00E9").And.Contain(@"\u0026");

        using JsonDocument parsed = JsonDocument.Parse(frame.AsSpan(9).ToArray());
        parsed.RootElement.GetProperty("kwargs").GetProperty("path").GetString()
              .Should().Be("/usb/Le téstè & stüff.bgcode", "the two encodings differ only in bytes");
    }

    /// <summary>
    /// A caller that says nothing about the peer gets the encoding every known client reads, not the
    /// stock one.
    /// </summary>
    /// <remarks>
    /// The conservative direction, and the same reasoning as <see cref="PrinterDialect.For"/> treating
    /// an unrecognised client as Buddy: a new call path that forgets to pass a dialect should produce
    /// bytes that work, rather than bytes that work only on hardware nobody has yet.
    /// </remarks>
    [Fact]
    public void EncodeWithNoDialectDoesNotEscape()
    {
        byte[] frame = CommandWireEncoder.Encode(7, new SendFileInfo { Path = "/usb/stüff.bgcode" });

        Encoding.UTF8.GetString(frame, 9, frame.Length - 9).Should().Contain("stüff");
    }
}
