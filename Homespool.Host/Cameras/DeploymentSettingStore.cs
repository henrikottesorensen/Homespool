using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Cameras;

/// <summary>
/// Reads and writes the single row of deployment settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row is created on first read rather than seeded.</b> Seeding would put the defaults in a
/// migration, where changing one later means either a data migration or two places disagreeing about
/// what "unset" means. Creating it on demand keeps the defaults in the entity, which is where
/// somebody reading the code looks for them.
/// </para>
/// <para>
/// It lives beside the cameras because that is what needs it. If a second unrelated setting ever
/// earns a column here, this moves somewhere neutral — but moving it then is cheaper than guessing
/// now at a home for a thing with one caller.
/// </para>
/// </remarks>
public sealed class DeploymentSettingStore
{
    private readonly HomespoolDbContext _dbContext;
    private readonly TimeProvider _time;

    public DeploymentSettingStore(HomespoolDbContext dbContext, TimeProvider time)
    {
        _dbContext = dbContext;
        _time = time;
    }

    /// <summary>
    /// The settings, creating the row with its defaults if this deployment has never had one.
    /// </summary>
    public async Task<DeploymentSetting> GetAsync(CancellationToken cancellationToken)
    {
        DeploymentSetting? settings = await _dbContext.DeploymentSettings
                                                      .FirstOrDefaultAsync(
                                                          row => row.Id == DeploymentSetting.SingletonId,
                                                          cancellationToken)
                                                      .ConfigureAwait(false);

        if (settings is not null)
        {
            return settings;
        }

        settings = new DeploymentSetting { UpdatedAt = _time.GetUtcNow() };

        _dbContext.DeploymentSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return settings;
    }

    /// <summary>
    /// Records whether the stream server may contact a public STUN server.
    /// </summary>
    /// <remarks>
    /// <b>Writing this does not apply it.</b> The sidecar has to be told and restarted before the
    /// change reaches a single offer — see <see cref="WebRtcSidecarWriter"/> — and separating the two
    /// is deliberate: the record of what was chosen must survive a sidecar that could not be reached
    /// at that moment, so the next start applies it rather than losing it.
    /// </remarks>
    public async Task SetStunEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        DeploymentSetting settings = await GetAsync(cancellationToken).ConfigureAwait(false);

        settings.WebRtcStunEnabled = enabled;
        settings.UpdatedAt = _time.GetUtcNow();

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
