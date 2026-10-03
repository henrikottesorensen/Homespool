using System;
using System.Globalization;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

using SimpleBase;

using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// The framework's authenticator token provider, except that a code is accepted once: a right code
/// spends its time step, and a code for that step or an earlier one is refused afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>The framework's provider accepts a code as often as it is presented</b> until it falls out of the
/// window, two 30-second steps either side of now, and RFC 6238 section 5.2 says a verifier must not
/// accept an OTP a second time. Whoever sees a code - over a shoulder, or in a log - can then use it
/// after its owner has, to finish a sign-in or confirm a step-up.
/// </para>
/// <para>
/// <b>Which step matched is what has to be recorded, and the framework's check does not say.</b> It
/// answers only whether some step in the window matched, and reads the clock itself, so the step is
/// found here by computing each one's code with <see cref="Rfc6238"/>, and spent through
/// <see cref="HSUserManager.SpendAuthenticatorStepAsync"/> as one conditional update, which parallel
/// requests with one code cannot all pass.
/// </para>
/// <para>
/// <b>The window is the framework's</b>, so an authenticator whose clock the framework tolerated is
/// still tolerated. A code that matches two steps in it is taken as the later one, which is the one
/// its holder's app is showing or about to.
/// </para>
/// <para>
/// One code serves one act, so somebody signing in and then confirming a step-up within the same
/// 30 seconds waits for the next code.
/// </para>
/// </remarks>
public sealed class HSAuthenticatorTokenProvider : AuthenticatorTokenProvider<HSUser>
{
    /// <summary>The length of one time step, in seconds: what every authenticator app uses.</summary>
    private const int StepSeconds = 30;

    /// <summary>How many steps either side of now a code is accepted from: the framework's window.</summary>
    private const int WindowSteps = 2;

    private readonly TimeProvider _time;
    private readonly ILogger<HSAuthenticatorTokenProvider> _logger;

    public HSAuthenticatorTokenProvider(TimeProvider time, ILogger<HSAuthenticatorTokenProvider> logger)
    {
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Needs <see cref="HSUserManager"/>, and throws for any other manager rather than accept a code it
    /// cannot spend.
    /// </remarks>
    public override async Task<bool> ValidateAsync(string purpose, string token, UserManager<HSUser> manager, HSUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);

        if (manager is not HSUserManager users)
        {
            throw new NotSupportedException("Spending an authenticator code needs HSUserManager.");
        }

        string? key = await users.GetAuthenticatorKeyAsync(user);

        if (key is null || !int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
        {
            return false;
        }

        byte[] keyBytes = Base32.Rfc4648.Decode(key);
        long now = _time.GetUtcNow().ToUnixTimeSeconds() / StepSeconds;

        for (long step = now + WindowSteps; step >= now - WindowSteps; step--)
        {
            if (Rfc6238.ComputeTotp(keyBytes, (ulong)step) != code)
            {
                continue;
            }

            if (await users.SpendAuthenticatorStepAsync(user, step))
            {
                return true;
            }

            _logger.LogInformation("Authenticator code refused for user {UserId}: a code for that time step or a later one has already been used.", user.Id);

            return false;
        }

        return false;
    }
}
