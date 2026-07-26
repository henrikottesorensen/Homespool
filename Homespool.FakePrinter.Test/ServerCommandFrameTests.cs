using System;
using System.Linq;
using System.Text;

using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The frame codec against <c>receive_command</c>'s contract (connect.cpp:440-521 at the pinned
/// ref): 9-byte header, hex command id, the five type characters, and the exact rejection each
/// malformation earns.
/// </summary>
public class ServerCommandFrameTests
{
    /// <summary>A well-formed J frame parses into kind, id and payload.</summary>
    [Fact]
    public void AWellFormedJsonFrameParses()
    {
        byte[] message = Frame('J', "0000002A", """{"command": "PAUSE_PRINT"}""");

        FrameParseResult result = ServerCommandFrame.Parse(message);

        result.Broken.Should().BeNull();
        result.Frame!.Kind.Should().Be(ServerCommandKind.Json);
        result.Frame.CommandId.Should().Be(0x2A);
        result.Frame.PayloadText().Should().Be("""{"command": "PAUSE_PRINT"}""");
        result.Frame.TryGetJsonCommandName().Should().Be("PAUSE_PRINT");
    }

    /// <summary>
    /// Shorter than the 9-byte header is refused with the firmware's exact reason, under command
    /// id 0 - there is no id to report it under (connect.cpp:450-452).
    /// </summary>
    [Fact]
    public void AMessageShorterThanTheHeaderIsBrokenUnderCommandIdZero()
    {
        FrameParseResult result = ServerCommandFrame.Parse(Encoding.ASCII.GetBytes("J1234"));

        result.Frame.Should().BeNull();
        result.Broken!.CommandId.Should().Be(0);
        result.Broken.Reason.Should().Be("Message too short to contain header");
    }

    /// <summary>Non-hex in the id field is the firmware's "Could not parse command ID", id 0.</summary>
    [Fact]
    public void ANonHexCommandIdIsBrokenUnderCommandIdZero()
    {
        FrameParseResult result = ServerCommandFrame.Parse(Frame('J', "XYZ!!123", "{}"));

        result.Frame.Should().BeNull();
        result.Broken!.CommandId.Should().Be(0);
        result.Broken.Reason.Should().Be("Could not parse command ID");
    }

    /// <summary>
    /// An unknown type character is refused - but the id parsed, so the rejection carries it
    /// (connect.cpp:518-521).
    /// </summary>
    [Fact]
    public void AnUnknownTypeCharacterIsBrokenUnderTheParsedId()
    {
        FrameParseResult result = ServerCommandFrame.Parse(Frame('Q', "000000FF", "{}"));

        result.Frame.Should().BeNull();
        result.Broken!.CommandId.Should().Be(0xFF);
        result.Broken.Reason.Should().Be("Unrecognized type of message");
    }

    /// <summary>All five real type characters parse to their kinds.</summary>
    [Theory]
    [InlineData('J', ServerCommandKind.Json)]
    [InlineData('G', ServerCommandKind.Gcode)]
    [InlineData('F', ServerCommandKind.ForcedGcode)]
    [InlineData('D', ServerCommandKind.Debug)]
    [InlineData('T', ServerCommandKind.TransferChunk)]
    public void EveryRealTypeCharacterParses(char kind, ServerCommandKind expected)
    {
        FrameParseResult result = ServerCommandFrame.Parse(Frame(kind, "00000001", "payload"));

        result.Frame!.Kind.Should().Be(expected);
    }

    /// <summary>A transfer chunk's payload is raw bytes, preserved untouched.</summary>
    [Fact]
    public void ATransferChunkPayloadIsPreservedByteForByte()
    {
        byte[] chunk = [0x00, 0xFF, 0x7F, 0x80, 0x01];
        byte[] message = Frame('T', "0000BEEF", string.Empty).Concat(chunk).ToArray();

        FrameParseResult result = ServerCommandFrame.Parse(message);

        result.Frame!.CommandId.Should().Be(0xBEEF);
        result.Frame.Payload.ToArray().Should().Equal(chunk);
    }

    /// <summary>
    /// Garbage JSON yields no command name but is distinguishable from valid JSON naming nothing -
    /// the two earn different firmware reasons ("Error parsing JSON" vs "Unknown command").
    /// </summary>
    [Fact]
    public void GarbageJsonAndCommandlessJsonAreDistinguishable()
    {
        FrameParseResult garbage = ServerCommandFrame.Parse(Frame('J', "00000001", "{{{not json"));
        FrameParseResult commandless = ServerCommandFrame.Parse(Frame('J', "00000002", """{"foo": 1}"""));

        garbage.Frame!.TryGetJsonCommandName().Should().BeNull();
        garbage.Frame.PayloadIsValidJson().Should().BeFalse();

        commandless.Frame!.TryGetJsonCommandName().Should().BeNull();
        commandless.Frame.PayloadIsValidJson().Should().BeTrue();
    }

    private static byte[] Frame(char kind, string hexId, string payload)
    {
        return Encoding.ASCII.GetBytes($"{kind}{hexId}").Concat(Encoding.UTF8.GetBytes(payload)).ToArray();
    }
}
