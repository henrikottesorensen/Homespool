using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// Replays a recorded printer-to-server stream document by document - the highest-fidelity
/// telemetry source, because replayed bytes encode no opinion of ours (see
/// <c>notes/fake-printer-harness.md</c>, mitigation #1). The committed redacted capture
/// (<c>Homespool.Host.Test/websocket.capture</c>) is the default diet; the CLI can point it at a
/// fuller private capture.
/// </summary>
/// <remarks>
/// The capture format interleaves server-to-printer command frames (a type char + 8 hex digits +
/// payload, e.g. <c>J00000140{...}</c>) into the printer's own output; those lines are stripped
/// before parsing, the same way <c>CaptureReplayTests</c> does it. What remains is split into
/// individual JSON documents so each goes out as one WebSocket message - the framing a real
/// printer produces (one render, one message; <c>connect.cpp:646-673</c>).
/// </remarks>
public sealed class CaptureReplaySource : ITelemetrySource
{
    private readonly IReadOnlyList<byte[]> _messages;
    private readonly TimeSpan _delayBetweenMessages;
    private int _position;

    /// <summary>
    /// Loads and splits the capture up front, so a malformed file fails at construction rather than
    /// mid-run.
    /// </summary>
    /// <param name="capturePath">Path to the capture file.</param>
    /// <param name="delayBetweenMessages">
    /// Delay between replayed messages. Zero (the default) replays as fast as the socket accepts -
    /// right for tests; the CLI passes something realistic.
    /// </param>
    public CaptureReplaySource(string capturePath, TimeSpan delayBetweenMessages = default)
    {
        _messages = Load(capturePath);
        _delayBetweenMessages = delayBetweenMessages;
    }

    /// <summary>How many documents the capture yielded - lets a test assert everything was sent.</summary>
    public int MessageCount => _messages.Count;

    /// <inheritdoc/>
    public byte[]? NextMessage(FakeDevice device)
    {
        if (_position >= _messages.Count)
        {
            return null;
        }

        return _messages[_position++];
    }

    /// <inheritdoc/>
    public TimeSpan DelayBeforeNext(FakeDevice device)
    {
        return _delayBetweenMessages;
    }

    private static List<byte[]> Load(string capturePath)
    {
        string raw = File.ReadAllText(capturePath);
        StringBuilder cleaned = new();

        foreach (string line in raw.Split('\n'))
        {
            if (IsCommandFrame(line))
            {
                continue;
            }

            cleaned.Append(line).Append('\n');
        }

        byte[] bytes = Encoding.UTF8.GetBytes(cleaned.ToString());
        List<byte[]> messages = [];
        ReadOnlySpan<byte> span = bytes;

        while (true)
        {
            span = span.TrimStart(" \t\r\n"u8);

            if (span.IsEmpty)
            {
                break;
            }

            Utf8JsonReader reader = new(span, isFinalBlock: true, default);

            try
            {
                // With default reader options TryParseValue throws on a non-JSON token rather than
                // returning false (see notes/housekeeping.md, "TryParseValue and trailing
                // whitespace") - so both the false return and the exception mean the same thing.
                if (!JsonDocument.TryParseValue(ref reader, out JsonDocument? document))
                {
                    throw new JsonException("No JSON token found.");
                }

                document.Dispose();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Capture '{capturePath}' has unparseable content after {messages.Count} documents.",
                    exception);
            }

            messages.Add(span[..(int)reader.BytesConsumed].ToArray());
            span = span[(int)reader.BytesConsumed..];
        }

        return messages;
    }

    private static bool IsCommandFrame(string line)
    {
        if (line.Length < 9 || !"JGFDT".Contains(line[0], StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = 1; i <= 8; i++)
        {
            if (!Uri.IsHexDigit(line[i]))
            {
                return false;
            }
        }

        return true;
    }
}
