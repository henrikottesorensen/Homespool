using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Homespool.Model.Entities;

/// <summary>
/// A single-use, email-bound, expiring invitation to join the server. Created by an administrator;
/// consumed once at accept time.
/// </summary>
/// <remarks>
/// The token is minted by <c>TokenService.GenerateToken()</c> and only its hash is stored, via
/// <c>TokenService.HashToken</c> (PBKDF2/SHA-384) — the same scheme that protects the printer
/// registration token at rest. (An earlier draft named <c>CodeGenerator</c>; that produces a base36
/// string which <c>TokenService.HashToken</c> rejects, so the invite reuses <c>GenerateToken</c>
/// instead — one existing scheme, end to end.) The plaintext is shown or mailed once at creation and
/// never persisted.
/// </remarks>
public class Invitation
{
    public int Id { get; set; }

    /// <summary>Hash of the invite token. The plaintext is never stored.</summary>
    public required string HashedToken { get; set; }

    /// <summary>
    /// The address the invite is bound to. The invitee accepts <em>as</em> this address and cannot
    /// change it, so it doubles as the created account's email.
    /// </summary>
    public required string Email { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the invite lapses. Default lifetime is 48 hours, configurable.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Set when the invite is accepted; null while outstanding. This is what makes an invite
    /// single-use — accept checks it is null and stamps it in the same step.
    /// </summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>User id of the administrator who issued the invite.</summary>
    public long InvitedBy { get; set; }

    /// <summary>
    /// The team the invitee joins, when the invite adds someone to an existing team. Null means the
    /// invite mints a brand-new account with its own default team. Those are the two accept shapes.
    /// </summary>
    public int? TeamId { get; set; }

    /// <summary>
    /// The account this invite gives back to its owner, or null for an ordinary invite that creates
    /// one. Set when an administrator issues a recovery for somebody who has lost their credentials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The account, not the address, is what a recovery is bound to.</b> An address identifies
    /// nobody here - it can be changed, and two accounts may hold one over time - so an invite that
    /// found its target by looking the address up could be retargeted by an address change made
    /// between issuing and redeeming. The id cannot.
    /// </para>
    /// <para>
    /// <b>A plain id, not a foreign key</b>, on the reasoning <see cref="Team.CreatedBy"/> gives: an
    /// invite is a record of something an administrator did, and it should not acquire a say in the
    /// lifetime of the account it names. An id that no longer resolves is a recovery that can no
    /// longer be redeemed, which is the correct outcome and needs no delete behaviour to arrange.
    /// </para>
    /// </remarks>
    public long? RecoversUserId { get; set; }

    /// <summary>
    /// Whether redeeming this recovery also clears the account's authenticator. False on every
    /// ordinary invite, and on a recovery unless the administrator said the person had lost their
    /// second factor as well as their password.
    /// </summary>
    /// <remarks>
    /// <b>A separate decision because it is a separate power.</b> Restoring a password gives an
    /// account back to somebody who can still prove possession of their authenticator; clearing the
    /// second factor as well hands whoever holds the link the whole account. Most recoveries do not
    /// need it, so it is asked for rather than assumed.
    /// </remarks>
    public bool ClearsTwoFactor { get; set; }

    [ForeignKey(nameof(TeamId))]
    public virtual Team? Team { get; set; }
}
