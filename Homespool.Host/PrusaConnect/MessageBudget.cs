using System;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// How many messages one printer connection may send: a token bucket holding
/// <see cref="PrusaConnectOptions.MessageBurst"/> tokens and refilling at
/// <see cref="PrusaConnectOptions.MessagesPerSecond"/>. <see cref="Take"/> answers how long the
/// caller must wait before handling the message it just took a token for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Waiting, never refusing.</b> The socket's read loop reads straight from the socket, so a loop
/// that waits stops reading and the sender's own TCP window fills: a printer over its budget is
/// slowed, loses nothing, and keeps its order, and nothing piles up here while it waits. Refusing
/// instead would drop the one printer's own events, and closing the connection would only be
/// retried.
/// </para>
/// <para>
/// <b>Why a budget at all:</b> every printer's telemetry and events share one bounded channel into
/// the writer, and it drops the oldest item when full. Without a budget one connection sending as
/// fast as it can fills that channel with its own messages, and every other printer's are the ones
/// dropped.
/// </para>
/// <para>
/// A bucket goes into debt rather than refusing: the token is always taken, and the wait is how long
/// the refill takes to pay it back. One per connection and used only by its read loop, so not
/// thread-safe.
/// </para>
/// </remarks>
public sealed class MessageBudget
{
    private readonly TimeProvider _timeProvider;
    private readonly double _perSecond;
    private readonly double _burst;
    private double _tokens;
    private long _refilledAt;

    /// <summary>Creates a full bucket.</summary>
    /// <param name="perSecond">The sustained rate, in messages a second; must be positive.</param>
    /// <param name="burst">How many messages may arrive back to back before any wait; at least one.</param>
    /// <param name="timeProvider">The clock the refill is measured on.</param>
    public MessageBudget(int perSecond, int burst, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burst);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        _perSecond = perSecond;
        _burst = burst;
        _tokens = burst;
        _refilledAt = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Spends one token and returns how long to wait before handling the message it paid for -
    /// <see cref="TimeSpan.Zero"/> while the bucket still held one.
    /// </summary>
    public TimeSpan Take()
    {
        long now = _timeProvider.GetTimestamp();
        double elapsedSeconds = _timeProvider.GetElapsedTime(_refilledAt, now).TotalSeconds;

        _refilledAt = now;
        _tokens = Math.Min(_burst, _tokens + (elapsedSeconds * _perSecond)) - 1;

        return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _perSecond);
    }
}
