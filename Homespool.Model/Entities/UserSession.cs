using System;

namespace Homespool.Model.Entities;

/// <summary>
/// One signed-in browser: the server's half of an application cookie. The cookie carries a secret, and
/// a request is signed in only while a row here matches it - so ending a session is deleting its row,
/// and the next request that browser makes is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a ticket store.</b> The cookie still carries the whole principal; this row carries what a
/// request is checked against. Every sign-in mints a new secret and a new row, so there is no session
/// key a browser can arrive with and have a sign-in adopt - which is what a ticket store's renewal of
/// the incoming key would have allowed.
/// </para>
/// <para>
/// <b><see cref="SecretHash"/> is indexed, and finding the row is the verification</b>, as for
/// <see cref="ApiToken.TokenHash"/> and for the same reasons. Hashed rather than kept as given because
/// the data-protection keys that seal the cookie live in the same database: with the raw secret here,
/// a copy of the file would be enough to mint a cookie for every live session.
/// </para>
/// <para>
/// <b>The row is live while it has not expired, its <see cref="SecurityStamp"/> is still the
/// account's, and the passkey it names is still on the account.</b> A stamp change or a revoked
/// passkey therefore ends a session on its next request without anything deleting the row; the
/// retention sweep removes such rows afterwards.
/// </para>
/// </remarks>
public class UserSession
{
    public long Id { get; set; }

    /// <summary>
    /// The session's public identifier - what a revoke carries, since <see cref="Id"/> counts up. Names
    /// the row, never authenticates it: that is <see cref="SecretHash"/>.
    /// </summary>
    public Guid Uuid { get; set; } = Guid.NewGuid();

    /// <summary>The account signed in. Cascades: a session outliving its account signs in nobody.</summary>
    public long UserId { get; set; }

    /// <summary>
    /// SHA-384 of the secret the cookie carries, base64url-encoded. Uniquely indexed: the lookup is the
    /// verification.
    /// </summary>
    public required string SecretHash { get; set; }

    /// <summary>
    /// The account's security stamp when this session was signed in or last refreshed. A session whose
    /// stamp is no longer the account's is over - which is how a password change, a removed login or a
    /// closed account ends every other browser.
    /// </summary>
    public required string SecurityStamp { get; set; }

    /// <summary>
    /// The passkey this session was signed in with, or null when it was not a passkey sign-in. A
    /// session whose passkey has been removed is over, and the account's other sessions are not.
    /// </summary>
    public byte[]? PasskeyCredentialId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the cookie this row answers for expires. Moved forward as the cookie is renewed, so it
    /// tracks a sliding session rather than bounding it from sign-in.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
