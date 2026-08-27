using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Serilog;
using Serilog.Core;

using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// An optional record of the messages exchanged with printers, both directions, written to its own
/// file as one JSON object per line.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one place message bodies are written to disk, and that is its whole purpose.</b>
/// Every other log site in the connection path deliberately records a wire name and an id and
/// nothing more, because a payload can carry anything. The rule is not relaxed here so much as
/// moved: the file is off by default, it is not the log an operator reads by accident, and
/// <see cref="IsSecretKey"/> is what keeps a credential out of it. Adding a field to that list is
/// cheaper than explaining a leaked token.
/// </para>
/// <para>
/// <b>Its own Serilog pipeline, not the application's.</b> Routing this through the ordinary logger
/// would put message bodies on the console sink and into whatever an operator pipes that to, which
/// is the opposite of the containment above. A separate pipeline also means the traffic log's own
/// volume cannot push anything out of the application log.
/// </para>
/// <para>
/// <b>Message-level, not frame-level.</b> Frame boundaries are gone before this can see them: the
/// controller presents the socket as a byte stream and the read loop parses documents out of it, so
/// there is nothing here that knows a message arrived in 184 pieces. Recovering that would mean
/// owning the receive loop instead of <c>WebSocketStream</c>, and the corpus says it would buy
/// little - 8 of 30 750 captured messages were fragmented at all.
/// </para>
/// </remarks>
public sealed class PrinterTrafficLog : IDisposable
{
    /// <summary>What replaces a redacted value. Not the empty string: an operator reading this needs
    /// to see that something was removed rather than that a field was absent.</summary>
    private const string Redacted = "<redacted>";

    /// <summary>
    /// Longest string value written verbatim. Past this a marker naming the original length replaces
    /// it.
    /// </summary>
    /// <remarks>
    /// This exists for one measured shape: a <c>FILE_INFO</c> whose <c>preview</c> is an 89 KB
    /// base64 PNG, in a message of 92 831 bytes. A thumbnail explains nothing about a misbehaving
    /// printer, and at that size a handful of them is the whole file. Applied to every string rather
    /// than to <c>preview</c> by name, so a future firmware field carrying something equally large
    /// is bounded without anyone having to notice it first.
    /// </remarks>
    private const int MaxStringLength = 512;

    /// <summary>
    /// Field names whose values never reach the file, matched case-insensitively at any depth and in
    /// either direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One list for both directions because the two halves of the problem are the same shape.
    /// Inbound, an <c>INFO</c> event carries <c>fingerprint</c>, <c>sn</c> and <c>api_key</c> - and
    /// the last of those is the printer's PrusaLink password. Outbound, <c>SET_TOKEN</c> carries a
    /// printer's replacement credential, <c>START_CONNECT_DOWNLOAD</c>'s <c>hash</c> is a live
    /// capability token, and <c>START_ENCRYPTED_DOWNLOAD</c> carries an AES key and its IV.
    /// </para>
    /// <para>
    /// <b>Matched by name, not by type.</b> A type-driven rule would cover today's commands and miss
    /// the next firmware field that happens to carry the same secret under the same name - and this
    /// file is the one somebody pastes into an issue.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_key",
        "fingerprint",
        "hash",
        "iv",
        "key",
        "password",
        "sn",
        "token",
    };

    private readonly Logger? _sink;
    private readonly bool _telemetry;

    public PrinterTrafficLog(IOptions<PrinterTrafficLogOptions> options, ILogger<PrinterTrafficLog> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        PrinterTrafficLogOptions settings = options.Value;

        _telemetry = settings.Telemetry;

        if (!settings.Enabled)
        {
            return;
        }

        // Three arguments here are load-bearing and none of them is the default.
        //
        // The output template renders the composed record and nothing else: Serilog's own formatters
        // would wrap it in an envelope of theirs, forking the shape away from what the capture
        // decoders read.
        //
        // retainedFileCountLimit is null against a default of 31, so nothing is ever deleted. A
        // record that expires before somebody thinks to look at it is not a record, and the case
        // this was built for was four occurrences spread over six days.
        //
        // buffered stays off because a traffic log's worst moment is the one before a crash, which
        // is exactly what a buffered write loses.
        _sink = new LoggerConfiguration()
                .WriteTo.File(settings.Path,
                              outputTemplate: "{Message:l}{NewLine}",
                              rollingInterval: RollingInterval.Day,
                              retainedFileCountLimit: null,
                              buffered: false)
                .CreateLogger();

        // At Warning because it is one: this writes message bodies to disk, and an operator who
        // turned it on to chase something last month should be told it is still running.
        logger.LogWarning("Printer traffic log is ON, writing to {Path}. Telemetry messages are {Telemetry}.",
                          settings.Path, _telemetry ? "included" : "excluded");
    }

    /// <summary>Whether anything is being recorded. Callers need not check - every record method
    /// returns immediately when it is off - but the hot paths read better for saying so.</summary>
    public bool IsEnabled => _sink is not null;

    /// <summary>
    /// Records one message as it arrived from a printer.
    /// </summary>
    /// <remarks>
    /// <b>Called before the message is deserialized</b>, so a message that cannot be parsed is in the
    /// file rather than absent from it. That is the case with the least other evidence: a malformed
    /// document closes the connection and leaves only an exception behind.
    /// </remarks>
    public void RecordInbound(int printerId, JsonElement root)
    {
        if (_sink is null || (!_telemetry && IsTelemetryShaped(root)))
        {
            return;
        }

        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            WriteEnvelope(writer, printerId, "p2s");
            writer.WritePropertyName("json");
            WriteRedacted(writer, root);
            writer.WriteEndObject();
        }

        Emit(buffer);
    }

    /// <summary>
    /// Records one command as it was handed to a printer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composed from the command's own wire name and arguments rather than from the encoded frame.
    /// The encoder is deterministic over exactly those two inputs, so the record says the same thing
    /// - and reading the frame back would mean encoding it twice and parsing it again to redact it.
    /// </para>
    /// <para>
    /// <b>Only commands that were handed over reach this.</b> A send that timed out or faulted never
    /// reached the printer, so it is not traffic; the actor logs those itself, at Error, where a
    /// failure belongs.
    /// </para>
    /// </remarks>
    public void RecordOutbound(int printerId, uint commandId, ISendableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_sink is null)
        {
            return;
        }

        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            WriteEnvelope(writer, printerId, "s2p");
            writer.WriteNumber("command_id", commandId);

            writer.WritePropertyName("json");
            writer.WriteStartObject();
            writer.WriteString("command", command.WireName);

            if (command is ISendableGcodeCommand gcodeCommand)
            {
                // The G-frame's body is the line itself rather than a JSON document. Written under a
                // key of its own so a reader can tell the two frame types apart, which the wire does
                // by its first byte and this file otherwise could not.
                writer.WritePropertyName("gcode");
                WriteStringValue(writer, gcodeCommand.Line);
            }
            else if (command.Arguments is { } arguments)
            {
                writer.WritePropertyName("kwargs");
                writer.WriteStartObject();

                foreach (KeyValuePair<string, object?> argument in arguments)
                {
                    if (IsSecretKey(argument.Key))
                    {
                        writer.WriteString(argument.Key, Redacted);

                        continue;
                    }

                    writer.WritePropertyName(argument.Key);
                    JsonSerializer.Serialize(writer, argument.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        Emit(buffer);
    }

    /// <summary>
    /// The fields every record carries: when, which printer, which way.
    /// </summary>
    /// <remarks>
    /// <c>t</c>, <c>iso</c> and <c>dir</c> are named as <c>tools/captures/decode_captures.py</c>
    /// writes them, so anything that reads a decoded capture reads this too. <c>printer</c> replaces
    /// that tool's <c>stream</c>: a capture is one TCP connection and this file is every printer at
    /// once, which is a difference worth naming rather than papering over.
    /// </remarks>
    private static void WriteEnvelope(Utf8JsonWriter writer, int printerId, string direction)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        writer.WriteNumber("t", now.ToUnixTimeMilliseconds() / 1000.0);
        writer.WriteString("iso", now.ToString("O"));
        writer.WriteNumber("printer", printerId);
        writer.WriteString("dir", direction);
    }

    /// <summary>
    /// Copies one element through, replacing secret values and shortening long strings.
    /// </summary>
    /// <remarks>
    /// Recursion is bounded by the parse that produced the element:
    /// <see cref="JsonDocument"/> enforces a maximum depth of 64, and nothing reaches this that was
    /// not parsed first.
    /// </remarks>
    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (IsSecretKey(property.Name))
                    {
                        writer.WriteString(property.Name, Redacted);

                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteRedacted(writer, property.Value);
                }

                writer.WriteEndObject();

                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteRedacted(writer, item);
                }

                writer.WriteEndArray();

                break;

            case JsonValueKind.String:
                WriteStringValue(writer, element.GetString());

                break;

            default:
                element.WriteTo(writer);

                break;
        }
    }

    /// <summary>Writes a string, or a marker naming its length when it is past
    /// <see cref="MaxStringLength"/>.</summary>
    private static void WriteStringValue(Utf8JsonWriter writer, string? value)
    {
        if (value is not null && value.Length > MaxStringLength)
        {
            writer.WriteStringValue($"<{value.Length} characters elided>");

            return;
        }

        writer.WriteStringValue(value);
    }

    private static bool IsSecretKey(string name)
    {
        return SecretKeys.Contains(name);
    }

    /// <summary>
    /// Whether this message is telemetry, for the volume switch alone.
    /// </summary>
    /// <remarks>
    /// <b>Not a second classifier.</b> <see cref="MessageDispatcher.Classify"/> owns what a message
    /// <em>is</em>, and this must not grow into a rival: it answers only "may this be skipped", and
    /// a message it guesses wrong about is one record too many or too few in a diagnostic file. The
    /// test is deliberately the cheap half of the dispatcher's - anything with an <c>event</c> or a
    /// <c>transfer</c> key is kept, everything else is treated as the 1 Hz stream.
    /// </remarks>
    private static bool IsTelemetryShaped(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
               && !root.TryGetProperty("event", out _)
               && !root.TryGetProperty("transfer", out _);
    }

    /// <summary>
    /// Writes the composed line.
    /// </summary>
    /// <remarks>
    /// The record is already complete JSON by the time it gets here; the sink's output template
    /// renders it and nothing else, which is what keeps the file one JSON object per line. The
    /// <c>:l</c> on the template is conventional rather than load-bearing - measured, and the file
    /// is byte-identical without it.
    /// </remarks>
    private void Emit(ArrayBufferWriter<byte> buffer)
    {
        _sink!.Information("{Line:l}", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public void Dispose()
    {
        // Flushes and closes the file. The sink is unbuffered, so this is about the handle rather
        // than about losing records.
        _sink?.Dispose();
    }
}
