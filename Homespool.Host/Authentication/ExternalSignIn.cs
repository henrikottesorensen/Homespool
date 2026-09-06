using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

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
/// <b>Two items ride the challenge round trip</b>: which provider was challenged, since the callback
/// path is one for all of them, and, when a signed-in account is linking or re-authenticating, which
/// account expects the answer, so a callback carrying somebody else's external cookie is not read as
/// this account's. The framework calls the second an XSRF key; it is an expected-account check.
/// </para>
/// </remarks>
public sealed class ExternalSignIn
{
    /// <summary>The challenge item naming the provider challenged.</summary>
    public const string LoginProviderItem = "Homespool.External.LoginProvider";

    /// <summary>The challenge item naming the account the answer is for, when a signed-in account asked.</summary>
    public const string ExpectedAccountItem = "Homespool.External.ExpectedAccount";

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
    /// provider was asked, and, for a signed-in account linking or re-authenticating, which account
    /// expects the answer.
    /// </summary>
    public static AuthenticationProperties ChallengeProperties(string provider, string? redirectUrl, string? expectedAccountId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);

        AuthenticationProperties properties = new() { RedirectUri = redirectUrl };
        properties.Items[LoginProviderItem] = provider;

        if (expectedAccountId is not null)
        {
            properties.Items[ExpectedAccountItem] = expectedAccountId;
        }

        return properties;
    }

    /// <summary>
    /// The provider's answer, read from the external cookie, or <see langword="null"/> when there is
    /// none, it names no provider or subject, or it was meant for an account other than
    /// <paramref name="expectedAccountId"/>.
    /// </summary>
    public async Task<ExternalLoginInfo?> InfoAsync(HttpContext context, string? expectedAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        AuthenticateResult external = await context.AuthenticateAsync(IdentityConstants.ExternalScheme);
        IDictionary<string, string?>? items = external.Properties?.Items;

        if (external.Principal is null || items is null || !items.TryGetValue(LoginProviderItem, out string? provider) || provider is null)
        {
            return null;
        }

        if (expectedAccountId is not null
            && (!items.TryGetValue(ExpectedAccountItem, out string? expected) || !string.Equals(expected, expectedAccountId, StringComparison.Ordinal)))
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

        switch (await _rules.PreSignInCheckAsync(user))
        {
            case SignInRefusal.LockedOut:
                _logger.LogInformation("Provider sign-in refused for user {UserId}: locked out.", user.Id);

                return ExternalSignInResult.LockedOut;

            case SignInRefusal.NotAllowed:
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
