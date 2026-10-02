using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Homespool.Data;

namespace Homespool.Host.Cameras;

/// <summary>
/// Removes the streams Homespool registered with the stream server for cameras it no longer has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a camera can outlive its row in the sidecar.</b> Removing a camera deletes the row and then
/// asks the sidecar to drop the stream, and that second step fails whenever the sidecar is restarting
/// or unreachable - after which nothing remembers the uuid to ask again. A stream left behind is not
/// inert: for a camera attached to this machine, the device reads as free again, and re-adding it
/// puts two streams on one device node - the loser retrying forever at a whole core, and the camera
/// silently missing.
/// </para>
/// <para>
/// <b>Only streams Homespool made, recognised by name.</b> Every stream this application registers is
/// named after its camera's uuid in canonical form, and nothing else is removed - a stream with any
/// other name might be somebody's hand-added experiment, and deleting it to enforce a symmetry nobody
/// asked for would lose their work. Matching on the source instead was considered and does not work:
/// the sidecar reports a running producer by its connection, whose address need not be the source it
/// was registered with, and a running producer on a contended device is exactly the case that matters.
/// </para>
/// <para>
/// <b>Each removal goes through <see cref="CameraStreamSync"/>, and that is what makes this safe to run
/// beside a save.</b> The rows read here only choose which streams to ask about; the sync reads the
/// camera's row again inside that camera's gate, and removes the stream only if there is still no row.
/// The sidecar is listed before the rows are read all the same - a camera's row is committed before
/// its stream is registered, so a listed stream whose row is missing here is one worth asking about.
/// </para>
/// <para>
/// <b>More than one deployment sharing one sidecar would sweep each other's cameras.</b> No supported
/// arrangement does that - each stack has its own - but two development servers pointed at one local
/// stream server would.
/// </para>
/// </remarks>
public sealed class CameraStreamSweeper
{
    private readonly HomespoolDbContext _dbContext;
    private readonly Go2RtcClient _streamServer;
    private readonly CameraStreamSync _sync;
    private readonly ILogger<CameraStreamSweeper> _logger;

    public CameraStreamSweeper(HomespoolDbContext dbContext,
                               Go2RtcClient streamServer,
                               CameraStreamSync sync,
                               ILogger<CameraStreamSweeper> logger)
    {
        _dbContext = dbContext;
        _streamServer = streamServer;
        _sync = sync;
        _logger = logger;
    }

    /// <summary>
    /// Removes every uuid-named stream no camera owns, and says how many the sidecar confirmed.
    /// </summary>
    /// <remarks>
    /// Does nothing when the sidecar cannot be listed: an unreachable sidecar is not one holding
    /// nothing, and the next save or start asks again.
    /// </remarks>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        IReadOnlySet<string>? names = await _streamServer.ListStreamNamesAsync(cancellationToken).ConfigureAwait(false);

        return names is null ? 0 : await SweepAsync(names, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes every uuid-named stream no camera owns from a listing the caller already has.
    /// </summary>
    /// <remarks>
    /// For a caller that had to wait for the sidecar to answer before it could list it, and would
    /// otherwise ask twice.
    /// </remarks>
    public async Task<int> SweepAsync(IReadOnlySet<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);

        List<Guid> named = names.Select(OwnedName)
                                .OfType<Guid>()
                                .ToList();

        if (named.Count == 0)
        {
            return 0;
        }

        List<Guid> owned = await _dbContext.Cameras
                                           .Where(camera => named.Contains(camera.Uuid))
                                           .Select(camera => camera.Uuid)
                                           .ToListAsync(cancellationToken)
                                           .ConfigureAwait(false);

        int removed = 0;

        foreach (Guid orphan in named.Except(owned))
        {
            if ((await _sync.SyncAsync(orphan, cancellationToken).ConfigureAwait(false)).Outcome ==
                StreamSyncOutcome.Removed)
            {
                _logger.LogInformation("Removed stream {Stream} from the stream server, which no camera owns.", orphan);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// The camera uuid a stream name is, or null for a name Homespool would not have written.
    /// </summary>
    /// <remarks>
    /// Canonical form only - lower case, hyphenated, no braces - because that is the one form
    /// <see cref="Go2RtcClient.PutStreamAsync"/> writes. A name that parses as a uuid in any other
    /// form was written by somebody else, and is theirs.
    /// </remarks>
    private static Guid? OwnedName(string name)
    {
        return Guid.TryParseExact(name, "D", out Guid uuid) &&
               string.Equals(name, uuid.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal) ?
            uuid :
            null;
    }
}
