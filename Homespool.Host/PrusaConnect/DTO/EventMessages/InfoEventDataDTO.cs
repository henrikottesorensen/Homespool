using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

/// <summary>
/// The <c>INFO</c> event's <c>data</c> object - the one event payload typed now rather than left
/// as raw JSON, because <c>Printer.Model</c>/<c>Firmware</c> are meant to come from it
/// (<c>phase-1-storage.md</c> §13). Every field is nullable: only <c>firmware</c>/<c>printer_type</c>/
/// <c>sn</c>/<c>appendix</c>/<c>fingerprint</c>/<c>nozzle_diameter</c>/<c>transfer_paused</c>/
/// <c>network_info</c>/<c>slots</c> look unconditional in firmware source, but that reading isn't
/// backed by a real multi-hardware capture the way the telemetry DTO is - so nothing here is
/// trusted as guaranteed present.
/// </summary>
public class InfoEventDataDTO
{
    [JsonPropertyName("firmware")]
    public string? Firmware { get; set; }

    [JsonPropertyName("printer_type")]
    public string? PrinterType { get; set; }

    [JsonPropertyName("sn")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("appendix")]
    public bool? Appendix { get; set; }

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }

    [JsonPropertyName("nozzle_diameter")]
    public float? NozzleDiameter { get; set; }

    [JsonPropertyName("transfer_paused")]
    public bool? TransferPaused { get; set; }

    /// <summary>
    /// <b>The printer's PrusaLink password</b> - not a Connect one, despite the field name. Firmware
    /// sends <c>creds.pl_password</c> here (render.cpp:349-351), and that same value is what
    /// PrusaLink's HTTP server accepts as <c>X-Api-Key</c> (<c>req_parser.cpp:227</c>,
    /// <c>api_key = server->get_password()</c>). Auto-generated on first boot if unset
    /// (<c>wui.cpp:83-85</c>), so in practice it is present rather than optional.
    /// </summary>
    /// <remarks>
    /// <b>Treat as a credential.</b> Combined with <c>network_info</c>'s address it grants full
    /// authenticated access to the printer's HTTP API - including <c>GET /usb/&lt;path&gt;</c>, which
    /// serves any file on the drive. That is the only route by which a file can be fetched
    /// <i>from</i> a printer; the Connect protocol has no such command. See
    /// <c>notes/transfer-protocol.md</c>, "The other direction".
    /// </remarks>
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    /// <summary>Only present when the printer has USB storage attached.</summary>
    [JsonPropertyName("storages")]
    public List<InfoStorageDTO>? Storages { get; set; }

    [JsonPropertyName("network_info")]
    public InfoNetworkDTO? NetworkInfo { get; set; }

    /// <summary>
    /// Static per-slot hardware config, 1-based keys, only the slots in use - not the live
    /// temperature/fan data telemetry's <c>"slot"</c> object carries (see
    /// <see cref="PrusaConnect.DTO.Telemetry.ToolTelemetryDTO"/>). Different shape, same numbering.
    /// </summary>
    [JsonPropertyName("tools")]
    public Dictionary<string, InfoToolDTO>? Tools { get; set; }

    /// <summary>
    /// XL only. Static configuration - different shape from telemetry's live <c>"enclosure"</c>
    /// object (see <see cref="PrusaConnect.DTO.Telemetry.EnclosureTelemetryDTO"/>).
    /// </summary>
    [JsonPropertyName("enclosure")]
    public InfoEnclosureDTO? Enclosure { get; set; }

    /// <summary>Only present on MMU-equipped builds.</summary>
    [JsonPropertyName("mmu")]
    public InfoMmuDTO? Mmu { get; set; }

    /// <summary>CoreOne/CoreOne L only.</summary>
    [JsonPropertyName("addon_power")]
    public bool? AddonPower { get; set; }

    [JsonPropertyName("slots")]
    public int? Slots { get; set; }
}

public class InfoStorageDTO
{
    [JsonPropertyName("mountpoint")]
    public string? MountPoint { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("read_only")]
    public bool ReadOnly { get; set; }

    [JsonPropertyName("free_space")]
    public long FreeSpace { get; set; }

    [JsonPropertyName("is_sfn")]
    public bool IsSfn { get; set; }
}

public class InfoNetworkDTO
{
    [JsonPropertyName("lan_mac")]
    public string? LanMac { get; set; }

    [JsonPropertyName("lan_ipv4")]
    public string? LanIpv4 { get; set; }

    [JsonPropertyName("wifi_ssid")]
    public string? WifiSsid { get; set; }

    [JsonPropertyName("wifi_mac")]
    public string? WifiMac { get; set; }

    [JsonPropertyName("wifi_ipv4")]
    public string? WifiIpv4 { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }
}

/// <summary><c>material</c> is the literal string <c>"---"</c> when no filament is set - a
/// firmware sentinel value, not something this DTO interprets.</summary>
public class InfoToolDTO
{
    [JsonPropertyName("nozzle_diameter")]
    public float NozzleDiameter { get; set; }

    [JsonPropertyName("high_flow")]
    public bool HighFlow { get; set; }

    [JsonPropertyName("hardened")]
    public bool Hardened { get; set; }

    [JsonPropertyName("material")]
    public string? Material { get; set; }
}

public class InfoEnclosureDTO
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("printing_filtration")]
    public bool PrintingFiltration { get; set; }

    [JsonPropertyName("post_print")]
    public bool PostPrint { get; set; }

    [JsonPropertyName("post_print_filtration_time")]
    public int PostPrintFiltrationTime { get; set; }

    [JsonPropertyName("filter_lifetime")]
    public int FilterLifetime { get; set; }

    [JsonPropertyName("filtration_filaments")]
    public List<string>? FiltrationFilaments { get; set; }
}

public class InfoMmuDTO
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
