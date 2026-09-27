using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

/// <summary>
/// The two gcode headers a <c>FILE_INFO</c> for a print file carries that describe its plate, read
/// out of the same answer <see cref="FileInfoEventDataDTO"/> reads firmware's own fields from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither is the printer's data.</b> Firmware streams a file's <c>; key = value</c> headers
/// through unparsed, so both are the slicer's output relayed verbatim - which is why
/// <see cref="ObjectsInfo"/> is a JSON document inside a string, and why both are
/// attacker-influenced and parsed with limits, by <c>PlateLayout.Parse</c>.
/// </para>
/// <para>
/// <b>Only these two, for the reason <see cref="FileInfoEventDataDTO"/> gives for having no
/// extension data</b>: the rest of the answer is hundreds of keys and a thumbnail, and a class naming
/// what it reads leaves all of that unmaterialised.
/// </para>
/// </remarks>
public class PlateInfoEventDataDTO
{
    /// <summary>
    /// Every cancellable object's name and bed outline, as <c>{"objects":[{"name":…,"polygon":[[x,y],…]}]}</c>
    /// in a string. Null for a file sliced without cancel-object labels.
    /// </summary>
    [JsonPropertyName("objects_info")]
    public string? ObjectsInfo { get; set; }

    /// <summary>
    /// The bed's corners in millimetres, <c>"0x0,250x0,250x210,0x210"</c>. <b>Present for plain
    /// gcode only</b>: a <c>.bgcode</c> keeps it in the compressed slicer block, which firmware does
    /// not relay.
    /// </summary>
    [JsonPropertyName("bed_shape")]
    public string? BedShape { get; set; }
}
