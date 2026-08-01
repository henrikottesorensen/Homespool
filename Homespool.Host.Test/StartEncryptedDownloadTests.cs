using System;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="StartEncryptedDownload"/>'s wire shape, pinned against what firmware's parser will
/// accept - <c>ARGS_ENC_DOWN</c> (command.cpp:87) and <c>decode_hex</c> (command.cpp:57-68) at the
/// pinned ref.
/// </summary>
/// <remarks>
/// Nothing has ever observed this command on a wire, so unlike its inline sibling there is no
/// capture to check against and these assertions are only as good as the source read behind them.
/// They are worth having anyway: every one of them is a rule that turns the whole command into a
/// <c>BrokenCommand</c> when broken, silently, with the printer's refusal being the only symptom.
/// </remarks>
public class StartEncryptedDownloadTests
{
    private static StartEncryptedDownload Command()
    {
        return new()
        {
            Path = "/usb/enctest.gcode",
            Key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            Iv = Convert.FromHexString("f0e0d0c0b0a090807060504030201000"),
            OriginalSize = 1024,
        };
    }

    [Fact]
    public void TheFourRequiredKwargsAreAllPresent()
    {
        // Act
        byte[] frame = CommandWireEncoder.Encode(1, Command());
        using JsonDocument payload = JsonDocument.Parse(frame.AsSpan(9).ToArray());

        // Assert
        JsonElement root = payload.RootElement;
        root.GetProperty("command").GetString().Should().Be("START_ENCRYPTED_DOWNLOAD");

        JsonElement kwargs = root.GetProperty("kwargs");
        kwargs.GetProperty("path").GetString().Should().Be("/usb/enctest.gcode");
        kwargs.GetProperty("orig_size").GetInt64().Should().Be(1024);

        // Any one of the four missing makes firmware discard the whole command.
        kwargs.TryGetProperty("key", out _).Should().BeTrue();
        kwargs.TryGetProperty("iv", out _).Should().BeTrue();
    }

    /// <summary>
    /// <c>decode_hex</c> refuses anything whose length is not exactly twice the 16-byte block, so
    /// this is the difference between a transfer and a refusal.
    /// </summary>
    [Fact]
    public void KeyAndIvAreThirtyTwoCharacterHexStrings()
    {
        // Act
        byte[] frame = CommandWireEncoder.Encode(1, Command());
        using JsonDocument payload = JsonDocument.Parse(frame.AsSpan(9).ToArray());

        // Assert
        JsonElement kwargs = payload.RootElement.GetProperty("kwargs");

        JsonElement key = kwargs.GetProperty("key");
        key.ValueKind.Should().Be(JsonValueKind.String, "firmware matches is_arg(\"key\", Type::String)");
        key.GetString().Should().Be("000102030405060708090a0b0c0d0e0f");

        JsonElement iv = kwargs.GetProperty("iv");
        iv.ValueKind.Should().Be(JsonValueKind.String);
        iv.GetString().Should().Be("f0e0d0c0b0a090807060504030201000");
    }

    /// <summary>
    /// <c>port</c> carries no <c>HasArg</c> flag, so firmware treats it as genuinely optional. Sending
    /// an explicit null would be a string-or-number mismatch rather than an absence.
    /// </summary>
    [Fact]
    public void PortIsOmittedRatherThanNullWhenUnset()
    {
        // Act
        byte[] frame = CommandWireEncoder.Encode(1, Command());
        using JsonDocument payload = JsonDocument.Parse(frame.AsSpan(9).ToArray());

        // Assert
        payload.RootElement.GetProperty("kwargs").TryGetProperty("port", out _).Should().BeFalse();
    }

    [Fact]
    public void PortIsANumberWhenSet()
    {
        // Arrange
        StartEncryptedDownload command = Command();
        command.Port = 8099;

        // Act
        byte[] frame = CommandWireEncoder.Encode(1, command);
        using JsonDocument payload = JsonDocument.Parse(frame.AsSpan(9).ToArray());

        // Assert
        JsonElement port = payload.RootElement.GetProperty("kwargs").GetProperty("port");
        port.ValueKind.Should().Be(JsonValueKind.Number, "firmware parses it with INT_ARG into a uint16_t");
        port.GetUInt16().Should().Be(8099);
    }

    /// <summary>
    /// The URL is derived from the IV and nothing else, lowercase, as <c>make_enc_url</c> builds it
    /// (planner.cpp:191-206) - <c>/f/</c>, 32 hex characters, <c>/raw</c>. That is 39 characters;
    /// firmware's <c>enc_url_len</c> of 40 counts the terminator, and it lands in a 41-byte buffer.
    /// </summary>
    [Fact]
    public void TheUrlPathIsTheIvInLowercaseHex()
    {
        // Act
        string url = Command().UrlPath;

        // Assert
        url.Should().Be("/f/f0e0d0c0b0a090807060504030201000/raw");
        url.Should().HaveLength(39);
    }
}
