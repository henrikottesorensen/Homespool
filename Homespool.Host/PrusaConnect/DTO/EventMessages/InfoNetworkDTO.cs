using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

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
