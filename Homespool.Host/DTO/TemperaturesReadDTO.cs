namespace Homespool.Host.DTO;

/// <summary>Every temperature the printer reports, °C.</summary>
/// <remarks>
/// The heaters carry a setpoint; the rest are sensors with nothing to aim at. The nozzle here is the
/// flat reading - on a toolchanger, each head's own is on its row in <c>tools</c>.
/// </remarks>
public class TemperaturesReadDTO
{
    public required HeaterReadDTO Nozzle { get; set; }

    public required HeaterReadDTO Bed { get; set; }

    public required HeaterReadDTO Chamber { get; set; }

    public float? Heatbreak { get; set; }

    public int? Enclosure { get; set; }

    public float? Psu { get; set; }

    public float? Ambient { get; set; }
}
