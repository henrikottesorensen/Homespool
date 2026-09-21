namespace Homespool.Host.DTO;

/// <summary>A printer asking for a person.</summary>
public class AttentionReadDTO
{
    /// <summary>Prusa's error code, where the printer gave one.</summary>
    public int? Code { get; set; }

    /// <summary>The printer's own words. Not translated.</summary>
    public string? Text { get; set; }
}
