using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

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
