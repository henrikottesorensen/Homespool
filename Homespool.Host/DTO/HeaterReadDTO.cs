namespace Homespool.Host.DTO;

/// <summary>A heater: what it measures, and what it has been asked for. °C.</summary>
public class HeaterReadDTO
{
    public float? Current { get; set; }

    /// <summary>The setpoint. Zero is a heater switched off; null is one the printer did not report.</summary>
    public float? Target { get; set; }
}
