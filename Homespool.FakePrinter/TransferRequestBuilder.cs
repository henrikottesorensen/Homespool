using System.Buffers;
using System.Text.Json;

namespace Homespool.FakePrinter;

/// <summary>
/// Renders an <see cref="InlineRequest"/> the way the firmware does
/// (Prusa-Firmware-Buddy <c>render.cpp:100-119</c> at the pinned ref), field for field and in the
/// same order.
/// </summary>
/// <remarks>
/// The <c>"transfer": "inline"</c> marker leading the object is what keeps these out of the server's
/// telemetry branch: the message carries neither <c>event</c> nor <c>state</c>, so without the marker
/// it would be parsed as telemetry and throw on the missing <c>state</c>, closing the socket
/// mid-upload. That was the trap <c>notes/transfer-protocol.md</c> flagged for the dispatcher.
/// </remarks>
public static class TransferRequestBuilder
{
    /// <summary>
    /// The constant firmware sends as <c>chunk</c> (render.cpp:112), commented "Relates both to size
    /// of the FS block". <b>Not</b> the transfer unit and not a size the server is asked to honour -
    /// the unit is <c>start</c>..<c>end</c>. Sent because a real printer sends it.
    /// </summary>
    public const int BlockAlignmentHint = 4096;

    /// <summary>Renders one request as the JSON message body.</summary>
    public static byte[] Build(InlineRequest request)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("transfer", "inline");

            if (request.Details is { } details)
            {
                writer.WriteString("hash", details.Hash);
                writer.WriteNumber("team_id", details.TeamId);
                writer.WriteNumber("transfer_id", details.TransferId);
            }

            writer.WriteNumber("chunk", BlockAlignmentHint);
            writer.WriteNumber("file_id", request.FileId);
            writer.WriteNumber("start", request.Start);
            writer.WriteNumber("end", request.End);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
