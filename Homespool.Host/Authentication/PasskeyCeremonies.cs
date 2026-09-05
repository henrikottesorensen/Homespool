using System;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Authentication;

/// <summary>
/// Carries a passkey ceremony's state between its two requests: the engine's own state, which names
/// the challenge and sometimes an account, in a data-protected cookie that is written when the
/// challenge is issued and spent when the answer arrives. One class for both ceremonies - the sign-in
/// assertion <see cref="PasskeyAuthenticationHandler"/> runs, and the registration attestation the
/// Manage page runs - so the two cannot drift in what they protect or how long they last.
/// </summary>
/// <remarks>
/// <para>
/// <b>The state never reaches the client in the clear.</b> It is plaintext JSON naming the challenge
/// and, for a bound ceremony, the account; in a hidden field it would let a caller choose its own
/// challenge and claim to be anybody. The cookie handler's data protection is what pays for the
/// secrecy, and its expiry is <see cref="PasskeyAuthenticationOptions.CeremonyLifetime"/>.
/// </para>
/// <para>
/// <b>A ceremony is spent once, server-side.</b> Deleting the cookie is an instruction to the
/// browser; <see cref="PasskeyCeremonyLedger"/> is what refuses a copy of the request presented
/// again. <b>And it is spent for one operation</b>: an attestation's state answered as an assertion,
/// or the reverse, is refused before the engine sees it.
/// </para>
/// <para>
/// <b>The cookie is scoped to the page that issued the challenge</b> unless the options name a path,
/// because the answer comes back to that same page and no other page has a reason to read it.
/// </para>
/// </remarks>
public sealed class PasskeyCeremonies
{
    /// <summary>The operation a sign-in ceremony carries.</summary>
    public const string Assertion = "assertion";

    /// <summary>The operation a registration ceremony carries.</summary>
    public const string Attestation = "attestation";

    /// <summary>
    /// What <see cref="Take"/> found: the engine's state to answer with, or why there is none.
    /// </summary>
    /// <param name="EngineState">The state the ceremony was started with, when it may be answered.</param>
    /// <param name="Reason">Why it may not be, for the log; empty when it may.</param>
    public readonly record struct Outcome(string? EngineState, string Reason)
    {
        /// <summary>Whether the ceremony may be answered.</summary>
        public bool Succeeded => EngineState is not null;

        internal static Outcome Taken(string engineState)
        {
            return new Outcome(engineState, string.Empty);
        }

        internal static Outcome Refused(string reason)
        {
            return new Outcome(null, reason);
        }
    }

    private const string StateItem = $"{PasskeyAuthenticationHandler.PasskeyPrefix}.State";
    private const string OperationItem = $"{PasskeyAuthenticationHandler.PasskeyPrefix}.Operation";
    private const string CeremonyIdItem = $"{PasskeyAuthenticationHandler.PasskeyPrefix}.CeremonyId";

    private readonly IOptionsMonitor<PasskeyAuthenticationOptions> _options;
    private readonly PasskeyCeremonyLedger _ledger;
    private readonly ISecureDataFormat<AuthenticationProperties> _format;

    public PasskeyCeremonies(IOptionsMonitor<PasskeyAuthenticationOptions> options,
                             IDataProtectionProvider dataProtection,
                             PasskeyCeremonyLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);

        _options = options;
        _ledger = ledger;

        // Purposed on this type alone, so nothing else in the deployment can mint a ceremony cookie.
        _format = new PropertiesDataFormat(dataProtection.CreateProtector(typeof(PasskeyCeremonies).FullName!, "Ceremony"));
    }

    private PasskeyAuthenticationOptions Options => _options.Get(Schemes.Passkey);

    private TimeProvider Clock => Options.TimeProvider ?? TimeProvider.System;

    /// <summary>
    /// Starts a ceremony: records it in the ledger and writes the cookie carrying
    /// <paramref name="engineState"/> for <paramref name="operation"/>, to be answered within the
    /// ceremony lifetime.
    /// </summary>
    public void Begin(HttpContext context, string operation, string engineState)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(engineState);

        DateTimeOffset now = Clock.GetUtcNow();
        AuthenticationProperties state = new()
        {
            IssuedUtc = now,
            ExpiresUtc = now.Add(Options.CeremonyLifetime),
            Items =
            {
                [StateItem] = engineState,
                [OperationItem] = operation,
            },
        };

        state.Items[CeremonyIdItem] = _ledger.Begin(now, state.ExpiresUtc.Value);

        CookieOptions cookie = CookieOptionsFor(context);
        cookie.Expires = state.ExpiresUtc;

        context.Response.Cookies.Append(Options.CeremonyCookie.Name!, _format.Protect(state), cookie);
    }

    /// <summary>
    /// Ends the ceremony this request carries: deletes the cookie whatever happens next, spends the
    /// ceremony, and hands back the engine's state for <paramref name="operation"/> - or a refusal
    /// with a reason fit for a log line.
    /// </summary>
    public Outcome Take(HttpContext context, string operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        string? protectedState = context.Request.Cookies[Options.CeremonyCookie.Name!];

        if (string.IsNullOrEmpty(protectedState))
        {
            return Outcome.Refused("no ceremony is underway");
        }

        // Spent the moment it is read, on every path below.
        context.Response.Cookies.Delete(Options.CeremonyCookie.Name!, CookieOptionsFor(context));

        AuthenticationProperties? state = _format.Unprotect(protectedState);

        if (state is null ||
            !state.Items.TryGetValue(StateItem, out string? engineState) ||
            !state.Items.TryGetValue(OperationItem, out string? actual) ||
            !state.Items.TryGetValue(CeremonyIdItem, out string? ceremonyId) ||
            engineState is null || actual is null || ceremonyId is null)
        {
            return Outcome.Refused("the ceremony cookie could not be read");
        }

        if (state.ExpiresUtc is null || state.ExpiresUtc <= Clock.GetUtcNow())
        {
            return Outcome.Refused($"the ceremony expired at {state.ExpiresUtc:O}");
        }

        // Server-side as well as in the browser: a copy of this request taken before the cookie was
        // deleted is otherwise a complete answer that still verifies, since most authenticators never
        // advance the sign count that would notice a repeat.
        if (!_ledger.TrySpend(ceremonyId))
        {
            return Outcome.Refused("the ceremony was already answered, or was not issued by this server");
        }

        if (!string.Equals(actual, operation, StringComparison.Ordinal))
        {
            return Outcome.Refused($"the ceremony was a {actual}, answered as a {operation}");
        }

        return Outcome.Taken(engineState);
    }

    /// <summary>
    /// The cookie options, built the same way for writing and for deleting, since a delete that
    /// names a different path leaves the cookie standing.
    /// </summary>
    private CookieOptions CookieOptionsFor(HttpContext context)
    {
        CookieOptions cookie = Options.CeremonyCookie.Build(context);

        // Build() has already defaulted the path to the site root, which is why the page's path is
        // put back explicitly rather than filled in.
        cookie.Path = Options.CeremonyCookie.Path ?? context.Request.PathBase.Add(context.Request.Path).Value ?? "/";

        return cookie;
    }
}
