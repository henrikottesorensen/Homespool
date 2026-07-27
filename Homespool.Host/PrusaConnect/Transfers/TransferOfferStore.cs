using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;

using Microsoft.Extensions.Logging;

namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// Holds what has been offered for download, keyed by the hash the printer quotes back on the first
/// request of a transfer.
/// </summary>
/// <remarks>
/// <para>
/// In memory and therefore not durable, which is the right trade rather than a shortcut: an offer is
/// only meaningful while the connection that will consume it is alive, and a printer resuming a
/// transfer across a server restart gets a clean "unknown hash" failure instead of a stale file. The
/// alternative - persisting offers - would mean deciding when they expire, which is a problem this
/// does not have.
/// </para>
/// <para>
/// A <see cref="ConcurrentDictionary{TKey,TValue}"/> here does not contradict
/// <c>notes/concurrency-model.md</c>'s argument against them: this is a shared lookup table with no
/// per-entry workflow state, which is exactly what that type is for. The state that has a lifecycle -
/// which transfer is active, how much has been served - lives on the actor, single-threaded.
/// </para>
/// </remarks>
public sealed class TransferOfferStore : ITransferContentStore, ITransferOffers
{
    private readonly ConcurrentDictionary<string, string> _offers = new(StringComparer.Ordinal);
    private readonly ILogger<TransferOfferStore> _logger;

    public TransferOfferStore(ILogger<TransferOfferStore> logger)
    {
        _logger = logger;
    }

    public void Offer(string hash, string path)
    {
        _offers[hash] = path;
        _logger.LogDebug("Offered {Path} for transfer", path);
    }

    public void Revoke(string hash) => _offers.TryRemove(hash, out _);

    public bool TryOpen(string hash, [NotNullWhen(true)] out ITransferContent? content)
    {
        content = null;

        if (!_offers.TryGetValue(hash, out string? path))
        {
            return false;
        }

        try
        {
            content = new FileTransferContent(path);

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The file went away between being offered and being asked for. Treated as "not offered"
            // rather than thrown, because the caller's answer is the same either way - fail the
            // transfer - and this is the ordinary shape of a deleted upload.
            _logger.LogWarning(e, "Offered file could not be opened, treating the offer as unknown");
            _offers.TryRemove(hash, out _);

            return false;
        }
    }
}
