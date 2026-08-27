using System;
using System.Security.Cryptography;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

using Homespool.Model.Entities;

namespace Homespool.Host.Cameras;

/// <summary>
/// Keeps a camera's password out of the row it belongs to. The address is stored in the clear, the
/// password as Data Protection ciphertext beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A camera password cannot be hashed</b>, because it is presented on every connection - the same
/// property a Bambu access code has, and the first case in this
/// application where "hash it" is not the answer. Data Protection is already wired up and gives
/// AES-256-CBC + HMAC-SHA256 with key rotation, so the realistic alternative is not "nothing", it is
/// somebody writing AES code.
/// </para>
/// <para>
/// <b>Be honest about what this buys, because it is narrower than "encrypted at rest" sounds.</b> The
/// key ring is protected by a certificate in <c>data/certificates</c> and the database is
/// <c>data/Homespool.Sqlite</c> - the same volume. Anyone holding that volume holds both halves, so
/// a whole-box compromise and a backup of <c>data/</c> are both undefended, and nothing here should
/// ever be described to an operator as making a backup safe to hand over. What it does defend is
/// <i>partial</i> disclosure, which is the more commonly realised leak: a query result pasted into a
/// support thread, a log line, somebody opening the file in a database browser. "In the row" versus
/// "needs the key ring and the algorithm" is a real difference. Moving the certificate out of
/// <c>homespool-data</c> is what would widen it, and that is a deployment change rather than this one.
/// </para>
/// <para>
/// <b>A lost key ring degrades gracefully for this datum specifically</b> - the camera stops
/// streaming and somebody re-enters its password, unlike the sign-in cookies and reset tokens the
/// same keys protect. So <see cref="Reveal"/> answers null rather than throwing: a camera that cannot
/// be decrypted is a camera that does not stream, which is visible and fixable, where a faulted
/// reconciler would take every other camera down with it.
/// </para>
/// <para>
/// <b>The purpose string is versioned</b> so a future change of scheme can be told from this one.
/// Data Protection binds ciphertext to its purpose, so this string may never be edited casually -
/// changing it makes every stored password undecryptable.
/// </para>
/// </remarks>
public sealed class CameraCredentialProtector
{
    /// <summary>
    /// Binds this ciphertext to cameras. Never edit it: every stored password becomes unreadable.
    /// </summary>
    public const string Purpose = "Homespool.Cameras.Credential.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<CameraCredentialProtector> _logger;

    public CameraCredentialProtector(IDataProtectionProvider provider,
                                     ILogger<CameraCredentialProtector> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    /// <summary>
    /// Splits a submitted source, returning the address to store and the protected password to store
    /// beside it.
    /// </summary>
    /// <param name="source">The source as it will be used, credential and all.</param>
    public CameraCredential Split(string source)
    {
        CameraSourceParts parts = CameraSourceDisplay.SplitCredential(source);

        return new CameraCredential(parts.Address,
                                    parts.User,
                                    parts.Password is null ? null : _protector.Protect(parts.Password));
    }

    /// <summary>
    /// The source as the stream server needs it, with the stored password put back.
    /// </summary>
    /// <remarks>
    /// A camera saved before the password was split out still carries it inline in
    /// <see cref="Camera.Source"/> and has no secret, so it is returned unchanged and is split on its
    /// next save. That is the whole of the upgrade path.
    /// </remarks>
    /// <param name="camera">The camera to resolve.</param>
    public string Reveal(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (camera.CredentialSecret is null)
        {
            return camera.Source;
        }

        string? password;

        try
        {
            password = _protector.Unprotect(camera.CredentialSecret);
        }
        catch (CryptographicException exception)
        {
            // The key ring is gone or the row was written by a different deployment. Said once per
            // attempt rather than thrown, so one undecryptable camera does not stop the others.
            _logger.LogError(exception,
                             "Camera {CameraId} has a password this deployment cannot decrypt. It will not stream until the password is entered again.",
                             camera.Id);

            return camera.Source;
        }

        return CameraSourceDisplay.WithCredential(camera.Source, camera.CredentialUser, password);
    }
}

/// <summary>What is stored for a camera: an address in the clear, and a protected password.</summary>
/// <param name="Address">The source with no credential in it.</param>
/// <param name="User">The user name, stored in the clear - it is not the secret, and the edit form shows it.</param>
/// <param name="Secret">The password as Data Protection ciphertext, or null when there is none.</param>
public sealed record CameraCredential(string Address, string? User, string? Secret);
