using System;

namespace Homespool.Model.Entities;

/// <summary>
/// The key pair this deployment signs every Web Push request with (RFC 8292, VAPID). One row, minted
/// the first time a browser asks for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every subscription is bound to the public half.</b> A browser subscribes with this key, and its
/// push service then refuses any request not signed by the private half - so replacing the pair
/// orphans every subscription there is. It is never rotated on a schedule, and it lives in the database
/// rather than beside the certificates so that no backup can hold the subscriptions without the key
/// they were made for.
/// </para>
/// <para>
/// <b>The private half is Data Protection ciphertext</b>, as a camera password is, and with the same
/// limit: the key ring is in the same volume, so this defends a query result pasted somewhere, not a
/// copy of the whole disk.
/// </para>
/// </remarks>
public class VapidKey
{
    /// <summary>The one row's key. There is never a second.</summary>
    public const int SingletonId = 1;

    /// <summary>
    /// Generous: Data Protection ciphertext of a 32-byte key, base64url, is under 200 characters.
    /// </summary>
    public const int PrivateKeySecretMaxLength = 1024;

    /// <summary>The longest <see cref="PublicKey"/>: a 65-byte point is 87 base64url characters.</summary>
    public const int PublicKeyMaxLength = 128;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// The uncompressed P-256 public key, base64url - what a browser is handed as
    /// <c>applicationServerKey</c> and what each request names in its <c>k=</c> parameter.
    /// </summary>
    public required string PublicKey { get; set; }

    /// <summary>The 32-byte private scalar, base64url, protected by Data Protection.</summary>
    public required string PrivateKeySecret { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
