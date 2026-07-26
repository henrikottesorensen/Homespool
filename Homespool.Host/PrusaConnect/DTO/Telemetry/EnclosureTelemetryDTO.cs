using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.Telemetry;

/// <summary>
/// Wire values are all integers - unlike <see cref="ChamberTelemetryDTO.Temperature"/>, which is
/// fixed-point. Confirmed against firmware (<c>render.cpp</c>): the enclosure block renders with
/// <c>JSON_FIELD_INT</c> throughout, versus chamber's <c>JSON_FIELD_FFIXED</c> for temperature.
/// </summary>
public class EnclosureTelemetryDTO
{
    [JsonPropertyName("temp")]
    public int Temperature { get; set; }

    [JsonPropertyName("fan_rpm")]
    public int FanSpeed { get; set; }

    [JsonPropertyName("time_in_use")]
    public int TimeIsUse { get; set; }
}
