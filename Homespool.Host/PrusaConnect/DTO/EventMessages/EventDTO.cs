using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using Homespool.Model;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

/// <summary>
/// The envelope shared by every event message. <see cref="Data"/> stays raw for every event type
/// except <c>INFO</c> (see <see cref="InfoEventDataDTO"/>) - matching how
/// <c>PrinterEvent.Payload</c> already stores it verbatim rather than exploded into columns.
/// <c>FILE_INFO</c> in particular has a genuinely dynamic shape (arbitrary gcode metadata keys,
/// plus a field - <c>nozzle_diameter</c> - that is a float in <c>INFO</c> but a string here) that a
/// typed class would fight rather than model.
/// </summary>
public class EventDTO
{
    [JsonPropertyName("job_id")]
    public int? JobId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("machine_reason")]
    public string? MachineReason { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("dialog_id")]
    public uint? DialogId { get; set; }

    [JsonPropertyName("state")]
    public required string Status { get; set; }

    [JsonPropertyName("command_id")]
    public uint? CommandId { get; set; }

    [JsonPropertyName("transfer_id")]
    public int? TransferId { get; set; }

    [JsonPropertyName("event")]
    [JsonConverter(typeof(EventTypeJsonConverter))]
    public required PrinterEventType EventType { get; set; }

    /// <summary>
    /// Envelope keys this build does not model - see <see cref="UnknownFieldTracker"/>. This is the
    /// <em>envelope</em> only: <see cref="Data"/> stays a raw <see cref="JsonElement"/> and its keys
    /// never land here, which is deliberate and load-bearing (that remark explains why).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>
    /// Delegates the <c>event</c> field to <see cref="PrusaEventWireMapping"/>, which is the
    /// authority on Connect's vocabulary. A casing transform used to sit here; it only worked
    /// while our enum's members mirrored the SDK's, and <see cref="PrinterEventType"/> is
    /// deliberately free not to.
    /// </summary>
    public sealed class EventTypeJsonConverter : JsonConverter<PrinterEventType>
    {
        public override PrinterEventType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? wireValue = reader.GetString();
            if (wireValue is null)
            {
                throw new JsonException("The event field cannot be null.");
            }

            try
            {
                return PrusaEventWireMapping.Parse(wireValue);
            }
            catch (ArgumentOutOfRangeException e)
            {
                // The read loop treats JsonException as a protocol violation; an unknown event
                // word is exactly that, not an argument error in our own code.
                throw new JsonException(e.Message, e);
            }
        }

        public override void Write(Utf8JsonWriter writer, PrinterEventType value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(PrusaEventWireMapping.Format(value));
        }
    }
}
