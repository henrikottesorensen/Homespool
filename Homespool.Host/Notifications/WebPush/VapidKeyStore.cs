using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Lib.Net.Http.WebPush.Authentication;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Notifications.WebPush;

/// <summary>
/// The deployment's VAPID key pair: read once, minted the first time anything asks, and held for the
/// life of the process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Minted on first use rather than at startup</b>, so a deployment nobody enables notifications on
/// never has a key, and a start-up never waits on one.
/// </para>
/// <para>
/// <b>A key this deployment cannot decrypt is replaced, and the subscriptions made with it are
/// deleted.</b> That happens when the Data Protection key ring is lost. Every subscription was made
/// with the old public key, and a push service refuses a request signed by any other - so they are
/// unreachable whether or not their rows remain, and leaving them would show browsers as subscribed
/// that can never hear anything. Each browser subscribes again, with the new key, the next time its
/// owner opens the settings page.
/// </para>
/// </remarks>
public sealed class VapidKeyStore : IDisposable
{
    /// <summary>
    /// Binds the private key's ciphertext to this use. Never edit it: the stored key becomes
    /// unreadable, and every subscription with it.
    /// </summary>
    public const string Purpose = "Homespool.Notifications.VapidKey.v1";

    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtector _protector;
    private readonly IOptionsMonitor<WebPushOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<VapidKeyStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VapidCredentials? _current;

    public VapidKeyStore(IServiceScopeFactory scopes,
                         IDataProtectionProvider protection,
                         IOptionsMonitor<WebPushOptions> options,
                         TimeProvider time,
                         ILogger<VapidKeyStore> logger)
    {
        ArgumentNullException.ThrowIfNull(protection);

        _scopes = scopes;
        _protector = protection.CreateProtector(Purpose);
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The key pair, minting it if there is none yet.
    /// </summary>
    public async Task<VapidCredentials> GetAsync(CancellationToken cancellationToken)
    {
        if (_current is VapidCredentials current)
        {
            return current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _current ??= await LoadOrMintAsync(cancellationToken).ConfigureAwait(false);

            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _current?.Authentication.Dispose();
        _gate.Dispose();
    }

    private async Task<VapidCredentials> LoadOrMintAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        HomespoolDbContext db = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        VapidKey? stored = await db.VapidKeys.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (stored is not null)
        {
            string? privateKey = TryUnprotect(stored.PrivateKeySecret);

            if (privateKey is not null)
            {
                return Build(stored.PublicKey, privateKey);
            }

            // Not in one transaction, and it does not need to be: each step leaves a state the next
            // start converges from. Subscriptions gone and the key still here is this branch again;
            // the key gone as well is the mint below.
            int orphaned = await db.WebPushDestinations.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            db.VapidKeys.Remove(stored);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogError("The Web Push key cannot be decrypted by this deployment, so a new one is being made. " +
                             "{SubscriptionCount} browser subscriptions were made with the old key and were removed; " +
                             "each browser has to enable notifications again.",
                             orphaned);
        }

        return await MintAsync(db, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VapidCredentials> MintAsync(HomespoolDbContext db, CancellationToken cancellationToken)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = key.ExportParameters(includePrivateParameters: true);

        byte[] point = new byte[WebPushSubscriptionKeys.PublicKeyLength];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1);
        parameters.Q.Y!.CopyTo(point, 33);

        string publicKey = WebEncoders.Base64UrlEncode(point);
        string privateKey = WebEncoders.Base64UrlEncode(parameters.D!);

        db.VapidKeys.Add(new VapidKey
        {
            PublicKey = publicKey,
            PrivateKeySecret = _protector.Protect(privateKey),
            CreatedAt = _time.GetUtcNow(),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another process sharing the database minted first; the row it wrote is the key. There
            // is one process per database in every deployment, so this is a guard rather than a path.
            db.ChangeTracker.Clear();

            VapidKey winner = await db.VapidKeys.SingleAsync(cancellationToken).ConfigureAwait(false);

            return Build(winner.PublicKey,
                         TryUnprotect(winner.PrivateKeySecret) ??
                         throw new InvalidOperationException("The Web Push key another process wrote cannot be decrypted."));
        }

        _logger.LogInformation("Made this deployment's Web Push key.");

        return Build(publicKey, privateKey);
    }

    private VapidCredentials Build(string publicKey, string privateKey)
    {
        VapidAuthentication? authentication = new(publicKey, privateKey);

        try
        {
            authentication.Subject = _options.CurrentValue.Contact;
            authentication.TokenCache = new VapidTokenCache(_time);

            VapidCredentials credentials = new(publicKey, authentication);
            authentication = null;

            return credentials;
        }
        finally
        {
            authentication?.Dispose();
        }
    }

    private string? TryUnprotect(string secret)
    {
        try
        {
            return _protector.Unprotect(secret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
