using System;

using Homespool.Model.Entities;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Services;

/// <summary>
/// The single place that decides a newly created account's initial email-confirmation state.
/// </summary>
/// <remarks>
/// Every account-creation site (Register, ExternalLogin, first-run Setup, and invite acceptance once
/// it exists) injects this and calls <see cref="Apply"/> rather than repeating
/// <c>EmailConfirmed = !IsConfigured</c>. The point is one authoritative source: this rule is
/// security-relevant, so a change must not be applied to some creation paths and missed on others.
/// See <see cref="SmtpOptions.IsConfigured"/> for the full rationale behind the rule itself.
/// <para>
/// The decision is resolved <b>once at construction</b>, mirroring how <c>Program.cs</c> chooses the
/// email sender from <see cref="SmtpOptions.IsConfigured"/> at startup rather than per request. SMTP
/// configuration is fixed for the life of the process, so binding it here via <see cref="IOptions{T}"/>
/// (not a snapshot/monitor) keeps the sender and the confirmation policy from ever disagreeing, and
/// spares every caller from passing <see cref="SmtpOptions"/> in.
/// </para>
/// </remarks>
public sealed class AccountConfirmationPolicy
{
    private readonly bool _confirmAtCreation;

    public AccountConfirmationPolicy(IOptions<SmtpOptions> smtpOptions)
    {
        ArgumentNullException.ThrowIfNull(smtpOptions);

        _confirmAtCreation = !smtpOptions.Value.IsConfigured;
    }

    /// <summary>
    /// Sets <see cref="Microsoft.AspNetCore.Identity.IdentityUser{TKey}.EmailConfirmed"/> on a
    /// not-yet-created account. Confirmed at creation
    /// exactly when SMTP is absent - no confirmation mail can arrive then, and the account would
    /// otherwise be permanently unable to sign in with <c>RequireConfirmedAccount</c> on. The policy
    /// itself stays on either way; the decision is per account, never a policy flip, so configuring
    /// SMTP later cannot retroactively lock out accounts created without it.
    /// </summary>
    public void Apply(HSUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.EmailConfirmed = _confirmAtCreation;
    }
}
