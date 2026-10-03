using System;

namespace Homespool.Host.Notifications;

/// <summary>
/// What a notification says, composed once per recipient in their language and handed to whichever
/// channel delivers it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing in it belongs to a channel.</b> A browser shows <see cref="Title"/> and
/// <see cref="Body"/> and opens <see cref="Url"/> when tapped; a webhook will post the same fields as
/// JSON. A channel that needs something this does not say is a sign the message is missing a field,
/// not that the channel should work it out itself.
/// </para>
/// <para>
/// <b><see cref="Url"/> and <see cref="Tag"/> are short and plain, or the message is not made.</b>
/// They are identifiers our own code builds, never text a printer or a person wrote, so a long one or
/// one with any other character is a mistake here, and it throws. A channel with a size limit can then
/// shorten the title and body, and never has to shorten these: a cut path leads somewhere else, and a
/// cut tag can make two notifications replace each other.
/// </para>
/// </remarks>
/// <param name="Title">The headline, already localised.</param>
/// <param name="Body">The sentence under it, already localised.</param>
/// <param name="Url">
/// Where tapping it goes, as a path on this deployment (<c>/Printers/Detail/…</c>). Relative, because the
/// application is never told the name people reach it by; the browser resolves it against the origin
/// that subscribed.
/// </param>
/// <param name="Tag">What a later notification replaces: two with the same tag show as one, the newer.</param>
/// <param name="Urgency">How hard the delivery service should try to wake a device for it.</param>
/// <param name="TimeToLive">How long it is worth delivering at all; after that it is dropped undelivered.</param>
public sealed record NotificationMessage(string Title,
                                         string Body,
                                         string Url,
                                         string Tag,
                                         NotificationUrgency Urgency,
                                         TimeSpan TimeToLive)
{
    /// <summary>The longest <see cref="Url"/> a message may carry.</summary>
    public const int MaxUrlLength = 128;

    /// <summary>The longest <see cref="Tag"/> a message may carry.</summary>
    public const int MaxTagLength = 128;

    public string Url { get; init => field = Plain(value, MaxUrlLength); } = Plain(Url, MaxUrlLength);

    public string Tag { get; init => field = Plain(value, MaxTagLength); } = Plain(Tag, MaxTagLength);

    /// <summary>
    /// Whether <paramref name="c"/> may appear in a <see cref="Url"/> or a <see cref="Tag"/>: an ASCII
    /// letter or digit, or one of <c>/-_.~?=%#</c>. None of them is escaped in JSON, so each is one byte
    /// of a payload.
    /// </summary>
    public static bool IsPlain(char c)
    {
        return char.IsAsciiLetterOrDigit(c) || "/-_.~?=%#".Contains(c, StringComparison.Ordinal);
    }

    private static string Plain(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > maxLength)
        {
            throw new ArgumentException($"'{value}' is longer than {maxLength} characters.", nameof(value));
        }

        foreach (char c in value)
        {
            if (!IsPlain(c))
            {
                throw new ArgumentException($"'{value}' has a character other than an ASCII letter, a digit or /-_.~?=%#.",
                                            nameof(value));
            }
        }

        return value;
    }
}
