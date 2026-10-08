using System;

namespace Homespool.Host.Queue;

/// <summary>
/// The schedule the queue retries a refusal on - how long each attempt waits and how many identical
/// refusals hold the queue - shared by a refused transfer and a refused print start.
/// </summary>
/// <remarks>
/// <para>
/// <b>One schedule, because the two have the same shape.</b> In both, an unrecognised refusal is
/// retried rather than treated as terminal, which is right for one attempt and wrong for a thousand;
/// and in both, a printer answering the same way every time is not going to change its mind by being
/// asked again. The waits widen the window a slow transient has to clear in, cheaply - five retries
/// across a little under four minutes. Only the bound ends the loop; waiting alone just repeats the
/// same refusal less often.
/// </para>
/// <para>
/// What counts as a refusal, and which hold it leads to, stays with each path:
/// <see cref="TransferRetryRules"/> and <see cref="PrintStartRetryRules"/>.
/// </para>
/// </remarks>
public static class RefusalRetries
{
    /// <summary>
    /// How many identical refusals in a row hold the queue: the first, and five retries after it.
    /// </summary>
    public const int HoldAfter = 6;

    /// <summary>
    /// How long to wait after the first, second, third, fourth and fifth identical refusal.
    /// </summary>
    private static readonly TimeSpan[] Waits =
    [
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(12),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
    ];

    /// <summary>How long the next attempt waits after <paramref name="count"/> identical refusals.</summary>
    /// <param name="count">Identical refusals so far; one or more.</param>
    public static TimeSpan WaitAfter(int count)
    {
        return Waits[Math.Clamp(count, 1, Waits.Length) - 1];
    }

    /// <summary>
    /// Cuts printer-supplied text to a column's bound, without splitting a surrogate pair.
    /// </summary>
    /// <param name="value">The text, or null.</param>
    /// <param name="maxLength">The bound, in UTF-16 code units, as the column counts them.</param>
    public static string? Bound(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        int cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;

        return value[..cut];
    }
}
