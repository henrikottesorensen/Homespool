namespace Homespool.Host.Pages;

/// <summary>
/// A status message and what it reports - what the <c>_StatusMessage</c> partial renders.
/// </summary>
/// <param name="Message">The message, already localised; nothing is rendered when it is empty.</param>
/// <param name="Kind">What the message reports.</param>
public sealed record StatusAlert(string? Message, StatusKind Kind)
{
    /// <summary>
    /// The alert's contextual class. An <see cref="StatusKind.Undefined"/> kind is a
    /// warning, so a message nobody classified never reads as good news.
    /// </summary>
    public string AlertClass => Kind switch
    {
        StatusKind.Success => "alert-success",
        StatusKind.Info => "alert-info",
        StatusKind.Danger => "alert-danger",
        _ => "alert-warning",
    };
}
