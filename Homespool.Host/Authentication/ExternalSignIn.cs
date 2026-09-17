using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// The external-provider half of signing in: which providers there are, how a challenge to one is
/// framed so its answer can be told apart, how the answer is read back from the external cookie, and
/// what the answer is worth for an account that has the provider linked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transcribed from the framework's <c>SignInManager</c> at v10.0.11</b> -
/// <c>GetExternalAuthenticationSchemesAsync</c>, <c>ConfigureExternalAuthenticationProperties</c>,
/// <c>GetExternalLoginInfoAsync</c> and <c>ExternalLoginSignInAsync</c>. The provider's ticket lands
/// on <see cref="IdentityConstants.ExternalScheme"/> because the OpenID Connect handler is told to
/// sign in there; this reads it back and, when an account has the identity linked, hands the decision
/// to <see cref="LocalSignIn"/> the way the password page does.
/// </para>
/// <para>
/// <b>The provider key is the ticket's <see cref="JwtClaimTypes.Subject"/></b>, and nothing else. The
/// framework tried <c>ClaimTypes.NameIdentifier</c> first, for handlers that map inbound claims to the
/// SOAP-era names; this application's handlers do not, and a principal that carried both would be
/// one that a reader gets wrong.
/// </para>
/// <para>
/// <b>Three items ride the challenge round trip</b>: which provider was challenged, since the callback
/// path is one for all of them; which flow asked, so each callback reads only its own flow's answer;
/// and, when a signed-in account is linking or re-authenticating, which account expects the answer, so
/// a callback carrying somebody else's external cookie is not read as this account's. The framework
/// calls the last an XSRF key; it is an expected-account check. The flow is not optional company for
/// it: the account alone cannot tell a link from a re-authentication of that same account, and the
/// two are gated differently - see <see cref="ExternalRoundTrip"/>.
/// </para>
/// <para>
/// <b>The items are protected with the rest of the properties</b>, by the provider handler's state
/// parameter on the way out and by the external cookie on the way back, so a client cannot change
/// which flow it is in.
/// </para>
/// </remarks>
public sealed class ExternalSignIn
{
    /// <summary>The challenge item naming the provider challenged.</summary>
    public const string LoginProviderItem = "Homespool.External.LoginProvider";

    /// <summary>The challenge item naming the account the answer is for, when a signed-in account asked.</summary>
    public const string ExpectedAccountItem = "Homespool.External.ExpectedAccount";

    /// <summary>The challenge item naming the flow that asked, one of <see cref="ExternalRoundTrip"/>.</summary>
    public const string RoundTripItem = "Homespool.External.RoundTrip";

    private readonly IAuthenticationSchemeProvider _schemes;
    private readonly UserManager<HSUser> _users;
    private readonly LocalSignInRules _rules;
    private readonly LocalSignIn _signIn;
    private readonly ILogger<ExternalSignIn> _logger;

    public ExternalSignIn(IAuthenticationSchemeProvider schemes,
                          UserManager<HSUser> users,
                          LocalSignInRules rules,
                          LocalSignIn signIn,
                          ILogger<ExternalSignIn> logger)
    {
        _schemes = schemes;
        _users = users;
        _rules = rules;
        _signIn = signIn;
        _logger = logger;
    }

    /// <summary>
    /// The schemes a person can be sent to: every registered scheme with a display name, which is
    /// what the framework takes as the mark of an external provider.
    /// </summary>
    public async Task<IReadOnlyList<AuthenticationScheme>> ProvidersAsync()
    {
        return (await _schemes.GetAllSchemesAsync()).Where(scheme => !string.IsNullOrEmpty(scheme.DisplayName)).ToList();
    }

    /// <summary>
    /// The properties a challenge to <paramref name="provider"/> carries: where to come back to, which
    /// provider was asked, which flow asked, and, for a signed-in account linking or re-authenticating,
    /// which account expects the answer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="roundTrip"/> names no flow.</exception>
    /// <exception cref="ArgumentException">
    /// A link or a re-authentication names no account: both are answers for a signed-in account, and
    /// one that named none would be read by nobody.
    /// </exception>
    public static AuthenticationProperties ChallengeProperties(string provider,
                                                               string? redirectUrl,
                                                               ExternalRoundTrip roundTrip,
                                                               string? expectedAccountId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        roundTrip.RequireSet();

        if (roundTrip is not ExternalRoundTrip.SignIn && string.IsNullOrEmpty(expectedAccountId))
        {
            throw new ArgumentException($"A {roundTrip} round trip is for a signed-in account and must name it.", nameof(expectedAccountId));
        }

        AuthenticationProperties properties = new() { RedirectUri = redirectUrl };
        properties.Items[LoginProviderItem] = provider;
        properties.Items[RoundTripItem] = roundTrip.ToString();

        if (expectedAccountId is not null)
        {
            properties.Items[ExpectedAccountItem] = expectedAccountId;
        }

        return properties;
    }

    /// <summary>
    /// Restates the times on a provider's answer as it arrives: the provider's own <c>auth_time</c>, when
    /// it sent one, becomes <see cref="HSClaimTypes.ExternalAuthenticationTime"/>, and <c>auth_time</c> is
    /// set to <paramref name="now"/>, the moment the answer came back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>So <c>auth_time</c> is always present, and always this server's clock.</b> A provider is not
    /// obliged to report when it signed somebody in, and a check that runs only when it does is a check
    /// a provider switches off by leaving the claim out. The claim written here is issued by the local
    /// authority; the moved one keeps the provider as its issuer.
    /// </para>
    /// <para>
    /// <b>Whatever the provider sent under either name is replaced</b>, so a provider can neither supply
    /// this server's time nor add a second one for a reader to pick the wrong one of.
    /// </para>
    /// </remarks>
    public static void RestateAuthenticationTime(ClaimsPrincipal principal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity is not ClaimsIdentity primary)
        {
            throw new ArgumentException("A provider's answer carries an identity.", nameof(principal));
        }

        foreach (ClaimsIdentity identity in principal.Identities)
        {
            foreach (Claim claim in identity.FindAll(HSClaimTypes.ExternalAuthenticationTime).ToList())
            {
                identity.RemoveClaim(claim);
            }

            foreach (Claim claim in identity.FindAll(JwtClaimTypes.AuthenticationTime).ToList())
            {
                identity.RemoveClaim(claim);
                identity.AddClaim(new Claim(HSClaimTypes.ExternalAuthenticationTime, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer));
            }
        }

        primary.AddClaim(new Claim(JwtClaimTypes.AuthenticationTime,
                                   now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                                   ClaimValueTypes.Integer64));
    }

    /// <summary>
    /// The provider's answer, read from the external cookie, or <see langword="null"/> when there is
    /// none, it names no provider or subject, it was started by a flow other than
    /// <paramref name="roundTrip"/>, or it was meant for an account other than
    /// <paramref name="expectedAccountId"/>.
    /// </summary>
    /// <remarks>
    /// <b>An answer that names no flow is refused by every reader.</b> Only a round trip started before
    /// flows were named carries none, and it costs that person one more trip to the provider.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="roundTrip"/> names no flow.</exception>
    public async Task<ExternalLoginInfo?> InfoAsync(HttpContext context, ExternalRoundTrip roundTrip, string? expectedAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        roundTrip.RequireSet();

        AuthenticateResult external = await context.AuthenticateAsync(IdentityConstants.ExternalScheme);
        IDictionary<string, string?>? items = external.Properties?.Items;

        if (external.Principal is null || items is null || !items.TryGetValue(LoginProviderItem, out string? provider) || provider is null)
        {
            return null;
        }

        if (!items.TryGetValue(RoundTripItem, out string? askedBy) || !string.Equals(askedBy, roundTrip.ToString(), StringComparison.Ordinal))
        {
            _logger.LogWarning("An external answer from {LoginProvider} was refused: started by {AskedBy}, read by {RoundTrip}.",
                               provider,
                               askedBy ?? "no flow",
                               roundTrip);

            return null;
        }

        if (expectedAccountId is not null &&
            (!items.TryGetValue(ExpectedAccountItem, out string? expected) || !string.Equals(expected, expectedAccountId, StringComparison.Ordinal)))
        {
            return null;
        }

        string? providerKey = external.Principal.FindFirstValue(JwtClaimTypes.Subject);

        if (providerKey is null)
        {
            return null;
        }

        string displayName = (await ProvidersAsync()).FirstOrDefault(scheme => scheme.Name == provider)?.DisplayName ?? provider;

        return new ExternalLoginInfo(external.Principal, provider, providerKey, displayName)
        {
            AuthenticationTokens = external.Properties?.GetTokens(),
            AuthenticationProperties = external.Properties,
        };
    }

    /// <summary>
    /// What <paramref name="info"/> is worth: the account that has this provider identity linked is
    /// signed in, or left owing its second factor, or refused, as a password would be.
    /// </summary>
    public async Task<ExternalSignInResult> SignInAsync(HttpContext context, ExternalLoginInfo info, bool isPersistent)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(info);

        HSUser? user = await _users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);

        if (user is null)
        {
            return ExternalSignInResult.NoAccount;
        }

        // The account's standing, not the password lockout: a provider's answer cannot be guessed at
        // the login form, and a lockout that reached it would let a wrong password every five minutes
        // keep a provider-only account out - LocalSignInRules.PreSignInCheckAsync has the rule.
        if (await _rules.StandingCheckAsync(user) is not null)
        {
            _logger.LogInformation("Provider sign-in refused for user {UserId}: the account may not sign in.", user.Id);

            return ExternalSignInResult.NotAllowed;
        }

        if (await _signIn.OwesSecondFactorAsync(context, user))
        {
            await _signIn.BeginSecondFactorAsync(context, user, info.LoginProvider);

            return ExternalSignInResult.RequiresSecondFactor;
        }

        await _signIn.SignInAsync(context, user, isPersistent, info.LoginProvider);

        return ExternalSignInResult.Succeeded;
    }
}
