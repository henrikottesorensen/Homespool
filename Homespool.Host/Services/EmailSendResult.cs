namespace Homespool.Host.Services;

/// <summary>
/// Outcome of an attempt to send mail.
/// </summary>
/// <remarks>
/// Three states rather than a boolean because "nothing was sent" is ambiguous and the two cases need opposite
/// treatment: with no SMTP configured, not sending is the normal, expected path and the user should be told nothing,
/// whereas a configured server that failed is something the user needs to know about.
/// </remarks>
public enum EmailSendResult
{
    /// <summary>The message was handed to the mail server.</summary>
    Sent,

    /// <summary>
    /// No SMTP is configured, so nothing was sent and nothing was expected to be. Not an error.
    /// </summary>
    /// <remarks>
    /// In this mode accounts are created already confirmed (see <see cref="SmtpOptions.IsConfigured"/>), so no user
    /// is waiting on a message. Callers should stay silent rather than reporting a failure.
    /// </remarks>
    NotConfigured,

    /// <summary>SMTP is configured but the message could not be sent. The reason is in the log.</summary>
    Failed,
}
