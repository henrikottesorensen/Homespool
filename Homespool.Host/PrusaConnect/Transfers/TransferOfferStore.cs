using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// <see cref="CollectWithin"/> and <see cref="ResumeWithin"/>.
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
    /// How long a printer has to make its first request for an offer before the offer is closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Short, because nothing legitimate is slow here. A printer answers the command at once, a
    /// refusal revokes the offer there and then, and one that accepted has taken a transfer slot and
    /// fetches as its next step - so an offer still unopened after minutes belongs to a printer that
    /// acknowledged and then went away, and what it holds is worth reclaiming: an open file
    /// descriptor, and on the encrypted path a key and a URL that anyone who saw it can fetch.
    /// </para>
    /// <para>
    /// Minutes rather than seconds only to leave room for a printer that is busy with its USB
    /// stick when the command arrives.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan CollectWithin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an offer a printer has opened is kept, after the printer last let go of it, for the
    /// printer to come back to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Longer than <see cref="CollectWithin"/>, because a printer does come back: firmware retries a
    /// dropped download, keeps retrying for as long as it is printing, and writes the request to the
    /// USB stick so that a reboot resumes it under the same token.
    /// </para>
    /// <para>
    /// <b>Measured from when the offer was last given back, not from when it was made.</b> A
    /// download is several requests - firmware jumps between ranges as well as reconnecting - and
    /// the offer is idle between any two of them, so a limit on the offer's age would refuse the
    /// next request of a transfer that is simply large or slow. Firmware reads that 404 as a network
    /// fault and, while printing, retries it for as long as the print runs.
    /// </para>
    /// <para>
    /// <b>What a sliding limit cannot do is end</b>: whoever holds a token renews it by fetching,
    /// and on the encrypted path the token travels in a plain-HTTP URL. The ceiling on that is
    /// <see cref="PrusaConnectOptions.TransferOfferMaxLifetime"/>, which does run from when the
    /// offer was made.
    /// </para>
    /// <para>
    /// <b>Only idle offers are closed</b>, so no limit here cuts a request in progress; a borrowed
    /// offer is skipped and collected once it is given back.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan ResumeWithin = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, PinnedOffer> _offers = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<PrusaConnectOptions> _options;
    private readonly ILogger<TransferOfferStore> _logger;

    public TransferOfferStore(TimeProvider timeProvider,
                              IOptionsMonitor<PrusaConnectOptions> options,
                              ILogger<TransferOfferStore> logger)
    {
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Raised with the token of every offer taken out of service, whatever took it out - a revoke,
    /// a release, a sweep, or a re-offer under the same token.
    /// </summary>
    /// <remarks>
    /// This is how anything kept beside an offer follows it out. The store knows nothing of what
    /// that might be, which is the point: <see cref="EncryptedTransferOffers"/> holds a key per
    /// offer and must not outlive the bytes, and subscribing here is what makes that true by
    /// construction rather than by every caller remembering two revokes.
    /// </remarks>
    public event Action<string>? Retired;

    /// <inheritdoc />
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "The handle is deliberately long-lived: PinnedOffer owns it, and closes it when the offer is revoked or swept and nothing is reading it. Disposing here would defeat the pinning this method exists for.")]
    public bool Offer(string token, string path, int printerId)
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

        // A stored file's name is its name on disk, so the path is all it takes to say which file this is.
        PinnedOffer offer = new(content, _timeProvider, printerId, Path.GetFileName(path));

        // Re-offering a token replaces it. Tokens are random and minted per send, so this is the
        // theoretical case rather than the expected one - but leaking the old handle would be real.
        if (_offers.TryRemove(token, out PinnedOffer? replaced))
        {
            Retire(token, replaced);
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
            Retire(token, offer);
        }
    }

    /// <inheritdoc />
    public void Release(int printerId, string? hash)
    {
        if (hash is not null)
        {
            if (_offers.TryGetValue(hash, out PinnedOffer? offer) &&
                offer.PrinterId == printerId &&
                _offers.TryRemove(new KeyValuePair<string, PinnedOffer>(hash, offer)))
            {
                Retire(hash, offer);
            }

            return;
        }

        foreach (KeyValuePair<string, PinnedOffer> entry in _offers)
        {
            if (entry.Value.PrinterId != printerId || !entry.Value.IsIdle)
            {
                continue;
            }

            if (_offers.TryRemove(entry))
            {
                Retire(entry.Key, entry.Value);
            }
        }
    }

    /// <summary>
    /// The one way an offer leaves service, so that whatever follows it out is told every time.
    /// </summary>
    private void Retire(string token, PinnedOffer offer)
    {
        offer.Retire();
        Retired?.Invoke(token);
    }

    /// <inheritdoc />
    public bool TryOpen(string hash, int printerId, [NotNullWhen(true)] out ITransferContent? content)
    {
        content = null;

        // One answer for "unknown" and "not yours", as the interface promises. A printer that
        // presents a token it was never given learns nothing from the refusal.
        if (!_offers.TryGetValue(hash, out PinnedOffer? offer) || offer.PrinterId != printerId)
        {
            return false;
        }

        // The sweep runs on a timer, so an offer can be past its limit and still here. Checking on
        // the way in is what makes the limit exact for the one thing that matters to a stranger -
        // whether the token still opens anything - and the answer is the one the sweep would have
        // given a moment later.
        if (offer.IsAbandoned(_options.CurrentValue.TransferOfferMaxLifetime))
        {
            RetireAbandoned(hash, offer);

            return false;
        }

        content = offer.Borrow();

        return content is not null;
    }

    /// <summary>
    /// Closes offers nobody is reading and nobody came back for: one never opened within
    /// <see cref="CollectWithin"/>, one opened and then left idle for <see cref="ResumeWithin"/>,
    /// or one older than <see cref="PrusaConnectOptions.TransferOfferMaxLifetime"/>.
    /// </summary>
    /// <remarks>
    /// Called on a timer by <see cref="TransferOfferSweepService"/>, because the offer that matters
    /// most is the last one before a quiet spell and nothing else would ever reach it.
    /// <see cref="Offer"/> sweeps as well, which costs a scan of a handful of entries and bounds a
    /// store that is running without the service. Public so a test can run a pass without racing
    /// a hosted service's start.
    /// </remarks>
    public void SweepIdle()
    {
        // Read per pass, so a change to the setting applies to the offers already standing.
        TimeSpan maxLifetime = _options.CurrentValue.TransferOfferMaxLifetime;

        foreach (KeyValuePair<string, PinnedOffer> entry in _offers)
        {
            if (entry.Value.IsAbandoned(maxLifetime))
            {
                RetireAbandoned(entry.Key, entry.Value);
            }
        }
    }

    /// <summary>
    /// Every offer that has not ended: a file a printer is pulling, or one it has been told to fetch
    /// and has not yet opened.
    /// </summary>
    /// <remarks>
    /// This is what a restart would break. Offers live only in this process, so a printer coming back
    /// for one afterwards is refused as an unknown token. An offer past its limit and not yet swept is
    /// left out, since the next sweep ends it anyway. The same file offered twice to one printer - a
    /// resend while the first offer still stands - is one transfer to a reader, and listed once.
    /// </remarks>
    /// <returns>The printer and the file's name, per standing transfer.</returns>
    public IReadOnlyList<(int printerId, string fileName)> StandingOffers()
    {
        TimeSpan maxLifetime = _options.CurrentValue.TransferOfferMaxLifetime;

        return _offers.Values
                      .Where(offer => !offer.IsAbandoned(maxLifetime))
                      .Select(offer => (offer.PrinterId, offer.FileName))
                      .Distinct()
                      .ToList();
    }

    private void RetireAbandoned(string token, PinnedOffer offer)
    {
        if (_offers.TryRemove(new KeyValuePair<string, PinnedOffer>(token, offer)))
        {
            Retire(token, offer);
            _logger.LogInformation("Closed a transfer offer nobody collected");
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
        private readonly TimeProvider _timeProvider;
        private readonly DateTimeOffset _offeredAt;
        private DateTimeOffset _idleSince;
        private int _readers;
        private bool _opened;
        private bool _retired;

        public PinnedOffer(ITransferContent content, TimeProvider timeProvider, int printerId, string fileName)
        {
            _content = content;
            _timeProvider = timeProvider;
            _offeredAt = timeProvider.GetUtcNow();
            _idleSince = _offeredAt;
            PrinterId = printerId;
            FileName = fileName;
        }

        /// <summary>The printer the offer was made to, and the only one it opens for.</summary>
        public int PrinterId { get; }

        /// <summary>
        /// The file's name when it was offered. The offer keeps serving those bytes if the name moves
        /// on, so this is the name the transfer was started under.
        /// </summary>
        public string FileName { get; }

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

        /// <summary>
        /// Whether nothing is reading the offer and a limit has run out on it:
        /// <see cref="CollectWithin"/> if no printer ever opened it, otherwise
        /// <see cref="ResumeWithin"/> since it was last given back or
        /// <paramref name="maxLifetime"/> since it was made, whichever comes first.
        /// </summary>
        public bool IsAbandoned(TimeSpan maxLifetime)
        {
            lock (_gate)
            {
                if (_readers != 0)
                {
                    return false;
                }

                DateTimeOffset now = _timeProvider.GetUtcNow();

                if (!_opened)
                {
                    return now - _offeredAt >= CollectWithin;
                }

                return now - _idleSince >= ResumeWithin || now - _offeredAt >= maxLifetime;
            }
        }

        /// <summary>
        /// Lends the content out. The borrower disposes its view, not the handle. Null once the
        /// offer is retired: a sweep on another thread can take it out of service between a lookup
        /// finding it and this call, and a view over a closed handle would fail on its first read.
        /// </summary>
        public ITransferContent? Borrow()
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return null;
                }

                _readers++;
                _opened = true;
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

                if (_readers != 0)
                {
                    return;
                }

                _idleSince = _timeProvider.GetUtcNow();

                if (_retired)
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
