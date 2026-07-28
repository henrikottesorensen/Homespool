using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.Telemetry;

/// <summary>
/// The one shape both Full and Reduced telemetry messages deserialize into (Buddy's
/// <c>SendTelemetry::Mode</c>). Every field that is not always present is nullable, so a Reduced
/// message correctly leaves them <c>null</c> rather than a misleading default value - the merge
/// logic that reads this later must be able to tell "not sent" apart from "sent as zero".
/// </summary>
public class TelemetryDTO
{
    // --- Transfer block: flat at root, only present mid-transfer ---
    [JsonPropertyName("transfer_id")]
    public int? TransferId { get; set; }

    [JsonPropertyName("transfer_transferred")]
    public int? TransferTransferred { get; set; }

    [JsonPropertyName("transfer_time_remaining")]
    public int? TransferTimeRemaining { get; set; }

    [JsonPropertyName("transfer_progress")]
    public double? TransferProgress { get; set; }

    // --- Job block: flat at root, only present while a job exists ---
    [JsonPropertyName("job_id")]
    public int? JobId { get; set; }

    [JsonPropertyName("time_printing")]
    public int? TimePrinting { get; set; }

    [JsonPropertyName("time_remaining")]
    public int? TimeRemaining { get; set; }

    [JsonPropertyName("filament_change_in")]
    public int? TimeToFilamentChange { get; set; }

    [JsonPropertyName("progress")]
    public int? Progress { get; set; }

    [JsonPropertyName("fan_extruder")]
    public int? ExtruderFan { get; set; }

    [JsonPropertyName("fan_print")]
    public int? PrintFan { get; set; }

    [JsonPropertyName("filament")]
    public float? FilamentUsed { get; set; }

    // --- Full-mode fields ---
    [JsonPropertyName("temp_nozzle")]
    public float? NozzleTemperature { get; set; }

    [JsonPropertyName("temp_bed")]
    public float? BedTemperature { get; set; }

    [JsonPropertyName("target_nozzle")]
    public float? TargetNozzleTemperature { get; set; }

    [JsonPropertyName("target_bed")]
    public float? TargetBedTemperature { get; set; }

    [JsonPropertyName("speed")]
    public int? Speed { get; set; }

    [JsonPropertyName("flow")]
    public int? Flow { get; set; }

    [JsonPropertyName("material")]
    public string? Material { get; set; }

    /// <summary>Only present when there is no active job - mutually exclusive with the job block's
    /// fan/filament fields on the wire, though nothing here enforces that.</summary>
    [JsonPropertyName("axis_x")]
    public float? XAxis { get; set; }

    [JsonPropertyName("axis_y")]
    public float? YAxis { get; set; }

    [JsonPropertyName("axis_z")]
    public float? ZAxis { get; set; }

    [JsonPropertyName("enclosure")]
    public EnclosureTelemetryDTO? Enclosure { get; set; }

    [JsonPropertyName("chamber")]
    public ChamberTelemetryDTO? Chamber { get; set; }

    [JsonPropertyName("slot")]
    public SlotsTelemetryDTO? Slot { get; set; }

    // --- Prusa iX only, flat at root ---
    [JsonPropertyName("temp_heatbreak")]
    public float? HeatbreakTemperature { get; set; }

    [JsonPropertyName("temp_psu")]
    public float? PsuTemperature { get; set; }

    [JsonPropertyName("temp_ambient")]
    public float? AmbientTemperature { get; set; }

    [JsonPropertyName("extruder_fs_state")]
    public string? ExtruderFilamentSensorStatus { get; set; }

    [JsonPropertyName("remote_fs_state")]
    public string? RemoteFilamentSensorStatus { get; set; }

    // --- Always-present-ish tail fields ---
    [JsonPropertyName("command_id")]
    public uint? CommandId { get; set; }

    [JsonPropertyName("dialog_id")]
    public uint? DialogId { get; set; }

    [JsonPropertyName("state")]
    public required string Status { get; set; }

    /// <summary>
    /// Telemetry keys this build does not model, collected rather than discarded so
    /// <see cref="UnknownFieldTracker"/> can report them. Null when the message matched exactly,
    /// which is the normal case.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
