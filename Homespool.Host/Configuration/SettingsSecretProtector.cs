using System;
using System.Security.Cryptography;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Configuration;

/// <summary>
/// Keeps a credential stored in the settings file out of the clear. Everything else in that file is
/// an ordinary value; this is the one kind that is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same problem a camera password has, and deliberately the same answer.</b> An SMTP password
/// is presented on every connection, so it cannot be hashed - and
/// <see cref="Cameras.CameraCredentialProtector"/> already settled how this project stores such a
/// thing. Data Protection is wired up, gives AES-256-CBC + HMAC-SHA256 with key rotation, and the
/// realistic alternative is not "nothing" but somebody writing AES code.
/// </para>
/// <para>
/// <b>Its own purpose string, not the camera one.</b> Data Protection binds ciphertext to its
/// purpose, so sharing would mean a change made for one credential silently invalidated the other.
/// Never edit this string: every stored secret becomes undecryptable.
/// </para>
/// <para>
/// <b>Be honest about what this buys.</b> The key ring is protected by a certificate in
/// <c>data/certificates</c> and the settings file is <c>data/settings.json</c> - the same volume, so
/// anyone holding that volume holds both halves. A whole-box compromise and a backup of <c>data/</c>
/// are both undefended, and none of this may be described to an operator as making a backup safe to
/// hand over. What it does defend is partial disclosure, which is the leak that actually happens: a
/// file pasted into a support thread, a log line, somebody opening it to check an unrelated setting.
/// The permission bits are the first line and this is the second.
/// </para>
/// <para>
/// <b>A lost key ring costs outgoing mail and nothing else</b>, so <see cref="Reveal"/> answers null
/// rather than throwing: a deployment that cannot decrypt its mail password should still start, log
/// why, and let somebody type it again - unlike the sign-in cookies the same keys protect.
/// </para>
/// </remarks>
public sealed class SettingsSecretProtector
{
    /// <summary>
    /// Binds this ciphertext to settings. Never edit it: every stored secret becomes unreadable.
    /// </summary>
    public const string Purpose = "Homespool.Settings.Secret.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<SettingsSecretProtector> _logger;

    /// <summary>Creates the protector.</summary>
    /// <param name="provider">The application's data protection provider.</param>
    /// <param name="logger">Where an undecryptable value is reported.</param>
    public SettingsSecretProtector(IDataProtectionProvider provider, ILogger<SettingsSecretProtector> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    /// <summary>
    /// Turns a secret into the ciphertext to store.
    /// </summary>
    /// <param name="value">The secret as somebody typed it.</param>
    /// <returns>The ciphertext, or null when there is no secret to store.</returns>
    public string? Protect(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : _protector.Protect(value);
    }

    /// <summary>
    /// Reads a stored secret back.
    /// </summary>
    /// <param name="ciphertext">What was stored, or null or empty when nothing was.</param>
    /// <param name="name">The setting's path, named in the log when it cannot be read.</param>
    /// <returns>The secret, or null when there was none or it could not be decrypted.</returns>
    public string? Reveal(string? ciphertext, string name)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException exception)
        {
            // The key ring is gone, or this file was written by a different deployment. Logged
            // rather than thrown: the setting it belongs to stops working and says so, where a throw
            // here would stop the application starting at all.
            _logger.LogError(exception,
                             "{Setting} holds a value this deployment cannot decrypt. It will not be used until it is entered again.",
                             name);

            return null;
        }
    }
}
