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
    [JsonConverter(typeof(EventsJsonConverter))]
    public required Events EventType { get; set; }
}

/// <summary>
/// Maps <see cref="Events"/> to/from firmware's SCREAMING_SNAKE_CASE wire strings (e.g.
/// <c>FileInfo</c> &lt;-&gt; <c>"FILE_INFO"</c>). <see cref="JsonConverterAttribute"/> only
/// instantiates its target type via a parameterless constructor, so the naming policy has to be
/// baked into a subclass rather than passed at the attribute site.
/// </summary>
public sealed class EventsJsonConverter : JsonStringEnumConverter<Events>
{
    public EventsJsonConverter()
        : base(JsonNamingPolicy.SnakeCaseUpper)
    {
    }
}
