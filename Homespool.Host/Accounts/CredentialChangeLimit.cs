using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Homespool.Model;

namespace Homespool.Host.Accounts;

/// <summary>
/// How often the ways into one account may be changed: a provider linked or removed, a passkey added
/// or removed - once, then a <see cref="Cooldown"/> before the next.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each of those mails the owner</b> (<see cref="CredentialNotices"/>), and the recent proof they
/// take lasts ten minutes and is not used up by using it. Without a limit, a loop of add and remove
/// mails the owner at request rate: the inbox fills, the mail server's quota goes, and the one notice
/// that mattered is buried. So the changes themselves are limited, which bounds the mail with them.
/// </para>
/// <para>
/// <b>A cooldown on <see cref="AttemptLimiter"/>'s table</b>, <see cref="LimitedAction.ChangeSignIn"/>,
/// as the address page bounds its mail: kept across a restart, and lifted by an administrator clearing
/// the account's backoffs. One member for all of the changes, so alternating a passkey with a provider
/// does not double the allowance. The administrator's passkey revoke is outside it: an administrator
/// acting on somebody else's account, from a page with its own guards.
/// </para>
/// <para>
/// <b>Not the framework's rate limiter.</b> Its middleware runs before authentication, so a policy
/// cannot partition by account, and a page may carry only one of its attributes. This is checked in the
/// handlers instead, before anything is written.
/// </para>
/// </remarks>
public sealed class CredentialChangeLimit
{
    /// <summary>How long an account waits after one change before it may make another.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private readonly AttemptLimiter _attempts;
    private readonly TimeProvider _time;
    private readonly ILogger<CredentialChangeLimit> _logger;

    public CredentialChangeLimit(AttemptLimiter attempts, TimeProvider time, ILogger<CredentialChangeLimit> logger)
    {
        _attempts = attempts;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Whether <paramref name="userId"/> could make a change now, without starting the cooldown - for a
    /// step that comes before the change, so it can be refused before the person does anything.
    /// </summary>
    public async Task<bool> HasRoomAsync(long userId, CancellationToken cancellationToken)
    {
        if (await _attempts.RemainingLockoutAsync(userId, LimitedAction.ChangeSignIn, _time.GetUtcNow(), cancellationToken) is not { } remaining)
        {
            return true;
        }

        LogRefusal(userId, remaining);

        return false;
    }

    /// <summary>
    /// Starts <paramref name="userId"/>'s cooldown and answers true, or answers false without touching it
    /// while the last change's is still running.
    /// </summary>
    /// <remarks>
    /// Started before the change is written, so an attempt spends the wait whether or not it then
    /// succeeds, and a burst of posts cannot all pass the check while the first is being saved.
    /// </remarks>
    public async Task<bool> TryStartAsync(long userId, CancellationToken cancellationToken)
    {
        if (await _attempts.TryStartCooldownAsync(userId, LimitedAction.ChangeSignIn, _time.GetUtcNow(), Cooldown, cancellationToken) is { } remaining)
        {
            LogRefusal(userId, remaining);

            return false;
        }

        return true;
    }

    private void LogRefusal(long userId, TimeSpan remaining)
    {
        _logger.LogWarning("A change to how user {UserId} signs in was refused: the last one's cooldown has {Remaining} to run.",
                           userId,
                           remaining);
    }
}
