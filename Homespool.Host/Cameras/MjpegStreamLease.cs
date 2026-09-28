using System;
using System.Threading;

namespace Homespool.Host.Cameras;

/// <summary>
/// One open stream: disposing it gives the stream back, once however often it is disposed, and
/// <see cref="Stopped"/> is how it learns it was stopped from outside.
/// </summary>
public sealed class MjpegStreamLease : IDisposable
{
    private readonly CancellationTokenSource _stopped = new();
    private MjpegStreamLimiter? _owner;

    internal MjpegStreamLease(MjpegStreamLimiter owner, long userId, (long userId, Guid camera, Guid view)? key)
    {
        _owner = owner;
        UserId = userId;
        Key = key;
    }

    /// <summary>Cancelled when the view is stopped through <see cref="MjpegStreamLimiter.Stop"/>.</summary>
    public CancellationToken Stopped => _stopped.Token;

    internal long UserId { get; }

    internal (long userId, Guid camera, Guid view)? Key { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _owner, null) is { } owner)
        {
            owner.Release(this);
            _stopped.Dispose();
        }
    }

    internal void Cancel()
    {
        try
        {
            _stopped.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Given back between being found and being stopped: it has already ended.
        }
    }
}
