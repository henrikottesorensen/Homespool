namespace Homespool.Host.DTO;

/// <summary>The two fans every printer reports, RPM.</summary>
public class FansReadDTO
{
    public int? Extruder { get; set; }

    public int? Print { get; set; }
}
