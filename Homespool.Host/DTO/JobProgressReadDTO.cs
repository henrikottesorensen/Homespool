namespace Homespool.Host.DTO;

/// <summary>How far the running job has got, as the printer counts it.</summary>
public class JobProgressReadDTO
{
    /// <summary>Percent.</summary>
    public int? Progress { get; set; }

    /// <summary>Seconds.</summary>
    public int? TimePrinting { get; set; }

    /// <summary>Seconds, as the printer estimates them.</summary>
    public int? TimeRemaining { get; set; }

    /// <summary>Seconds until the next planned filament change, when the file has one.</summary>
    public int? TimeToFilamentChange { get; set; }
}
