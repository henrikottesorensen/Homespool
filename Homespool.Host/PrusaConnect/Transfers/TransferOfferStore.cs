using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// Holds what has been offered for download, keyed by the token the printer quotes back on the first
/// request of a transfer.
/// </summary>
/// <remarks>
/// <para>
/// <b>An offer holds an open handle, not a path</b> (2026-07-31). That is the whole point of it: a
/// file's bytes can change under its name now that overwrite exists, and the command we already sent
/// declared a length. Opening at offer time pins the <i>inode</i>, so an overwrite between the
/// command and the printer's first request replaces the name while this transfer keeps serving the
/// bytes it announced. Opening lazily instead would serve the new file against the old
/// <c>orig_size</c> and silently truncate whatever the printer wrote - every layer behaving
/// correctly and the result garbage. Deleting or renaming in that window remains a clean failure,
/// which is fine; only the silent case needed fixing.
/// </para>
/// <para>
/// <b>The handle is lent, not given away.</b> <see cref="TryOpen"/> hands out a borrowed view over
/// the one open handle and leaves the offer in place, because an offer deliberately outlives the
/// connection that consumes it: a printer that drops mid-transfer and is commanded again reopens the
/// same token, which is covered by a test. Handing ownership to the first consumer would have made
/// that second attempt fail. What the borrowing costs is a lifecycle - hence
/// <see cref="OfferLifetime"/>.
/// </para>
/// <para>
/// In memory and therefore not durable, which is the right trade rather than a shortcut: an offer is
/// only meaningful while the server that made it is running, and a printer resuming a transfer across
/// a restart gets a clean "unknown token" failure instead of a stale file. The alternative -
/// persisting offers - would mean deciding when they expire against a file that may since have been
/// replaced, which is the problem this pinning exists to avoid.
/// </para>
/// <para>
/// A <see cref="ConcurrentDictionary{TKey,TValue}"/> here does not contradict the general argument
/// against them: this is a shared lookup table with no
/// per-entry workflow state, which is exactly what that type is for. The state that has a lifecycle -
/// which transfer is active, how much has been served - lives on the actor, single-threaded.
/// </para>
/// </remarks>
public sealed class TransferOfferStore : ITransferContentStore, ITransferOffers
{
    /// <summary>
    /// How long an offer nobody is using is kept before its handle is closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before the handle existed this was not a problem: a consumed offer left a string in a
    /// dictionary, and nobody cared. A file descriptor is worth reclaiming, and nothing else would -
    /// a successful transfer never revokes its offer, and the printer that acked a command and then
    /// rebooted never comes back for it.
    /// </para>
    /// <para>
    /// <b>Only idle offers are swept</b>, so this cannot cut a transfer short however long it runs;
    /// a borrowed offer is skipped and collected once it is given back. The hour is therefore about
    /// how long a <i>finished or abandoned</i> offer lingers, not a limit on anything in progress.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan OfferLifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, PinnedOffer> _offers = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TransferOfferStore> _logger;

    public TransferOfferStore(TimeProvider timeProvider, ILogger<TransferOfferStore> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "The handle is deliberately long-lived: PinnedOffer owns it, and closes it when the offer is revoked or swept and nothing is reading it. Disposing here would defeat the pinning this method exists for.")]
    public bool Offer(string token, string path)
    {
        SweepIdle();

        FileTransferContent content;

        try
        {
            content = new FileTransferContent(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The file was there a moment ago - the caller looked it up - so this is a delete racing
            // a send. The caller can say so properly; here it is just "no".
            _logger.LogWarning(e, "Could not open a file to offer it for transfer");

            return false;
        }

        PinnedOffer offer = new(content, _timeProvider.GetUtcNow());

        // Re-offering a token replaces it. Tokens are random and minted per send, so this is the
        // theoretical case rather than the expected one - but leaking the old handle would be real.
        if (_offers.TryRemove(token, out PinnedOffer? replaced))
        {
            replaced.Retire();
        }

        _offers[token] = offer;
        _logger.LogDebug("Offered {Path} for transfer ({Length} bytes)", path, content.Length);

        return true;
    }

    /// <inheritdoc />
    public void Revoke(string token)
    {
        if (_offers.TryRemove(token, out PinnedOffer? offer))
        {
            offer.Retire();
        }
    }

    /// <inheritdoc />
    public bool TryOpen(string hash, [NotNullWhen(true)] out ITransferContent? content)
    {
        content = _offers.TryGetValue(hash, out PinnedOffer? offer) ? offer.Borrow() : null;

        return content is not null;
    }

    /// <summary>
    /// Closes offers nobody is reading and nobody came back for. Runs on the way in, so this needs no
    /// timer and no background service.
    /// </summary>
    private void SweepIdle()
    {
        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - OfferLifetime;

        foreach (KeyValuePair<string, PinnedOffer> entry in _offers)
        {
            if (entry.Value.OfferedAt > cutoff || !entry.Value.IsIdle)
            {
                continue;
            }

            if (_offers.TryRemove(entry))
            {
                entry.Value.Retire();
                _logger.LogInformation("Closed a transfer offer nobody collected");
            }
        }
    }

    /// <summary>
    /// One offered file: an open handle, when it was offered, and how many transfers are reading it.
    /// </summary>
    /// <remarks>
    /// The counting is what lets the handle be shared safely. Reads are positional
    /// (<see cref="FileTransferContent"/>), so two transfers over one handle do not interfere - the
    /// only hazard is closing it under one of them, which is exactly what the count prevents.
    /// </remarks>
    private sealed class PinnedOffer
    {
        private readonly Lock _gate = new();
        private readonly ITransferContent _content;
        private int _readers;
        private bool _retired;

        public PinnedOffer(ITransferContent content, DateTimeOffset offeredAt)
        {
            _content = content;
            OfferedAt = offeredAt;
        }

        public DateTimeOffset OfferedAt { get; }

        /// <summary>Whether nothing is currently reading this, and so it can be closed.</summary>
        public bool IsIdle
        {
            get
            {
                lock (_gate)
                {
                    return _readers == 0;
                }
            }
        }

        /// <summary>Lends the content out. The borrower disposes its view, not the handle.</summary>
        public ITransferContent Borrow()
        {
            lock (_gate)
            {
                _readers++;
            }

            return new BorrowedContent(this, _content);
        }

        /// <summary>
        /// Takes the offer out of service, closing it now if nothing is reading and otherwise leaving
        /// that to the last reader.
        /// </summary>
        public void Retire()
        {
            lock (_gate)
            {
                _retired = true;

                if (_readers == 0)
                {
                    _content.Dispose();
                }
            }
        }

        private void Return()
        {
            lock (_gate)
            {
                _readers--;

                if (_readers == 0 && _retired)
                {
                    _content.Dispose();
                }
            }
        }

        /// <summary>
        /// One transfer's view of a shared handle. Disposing it gives the loan back rather than
        /// closing the file, so the actor's ordinary disposal needs no special case.
        /// </summary>
        private sealed class BorrowedContent : ITransferContent
        {
            private readonly PinnedOffer _owner;
            private readonly ITransferContent _content;
            private int _returned;

            public BorrowedContent(PinnedOffer owner, ITransferContent content)
            {
                _owner = owner;
                _content = content;
            }

            public long Length => _content.Length;

            public ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken cancellationToken)
            {
                return _content.ReadAsync(destination, offset, cancellationToken);
            }

            public void Dispose()
            {
                // The actor disposes on several paths - transfer end, connection end, replacement -
                // and more than one can run for the same view. Only the first gives the loan back.
                if (Interlocked.Exchange(ref _returned, 1) == 0)
                {
                    _owner.Return();
                }
            }
        }
    }
}
