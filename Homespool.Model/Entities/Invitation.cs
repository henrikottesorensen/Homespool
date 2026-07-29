using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Homespool.Model.Entities;

/// <summary>
/// A single-use, email-bound, expiring invitation to join the server. Created by an administrator;
/// consumed once at accept time (phase-1.5 §15). This step defines the row only — issuing and
/// accepting invites is step 6.
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
    /// change it (§15 decision 3), so it doubles as the created account's email.
    /// </summary>
    public required string Email { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the invite lapses. Default lifetime is 48 hours, configurable (§15 decision 5).</summary>
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
    /// invite mints a brand-new account with its own default team — the two accept shapes in §15.
    /// </summary>
    public int? TeamId { get; set; }

    [ForeignKey(nameof(TeamId))]
    public virtual Team? Team { get; set; }
}
