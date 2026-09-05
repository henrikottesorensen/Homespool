using System;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Homespool.Host.Authentication;

public static class AuthenticationBuilderExtensions
{
    /// <summary>
    /// Registers the passkey scheme: a WebAuthn assertion verified by
    /// <see cref="PasskeyAuthenticationHandler"/>, reachable only by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Always registered, whether or not a relying-party id is configured.</b> Unlike the OpenID
    /// Connect scheme, nothing enumerates this one to decide what to offer - the login page asks
    /// <see cref="PasskeyAuthenticationOptions.Covers"/> per request, which is the check that also
    /// sees a host the id does not cover. An unconfigured scheme answers every challenge 404 and
    /// every assertion with a refusal, which is the behaviour a deployment without a name should get.
    /// </para>
    /// <para>
    /// <b>The relying-party id is read once</b>, from <c>Security:PasskeyServerDomain</c>, when the
    /// scheme's options are first built. It is a restart-graded setting on the settings page for the
    /// reason the page gives: every credential already enrolled is bound to the old value.
    /// </para>
    /// <para>
    /// <b>The engine's options are filled from this scheme's.</b> <see cref="IdentityPasskeyOptions"/>
    /// is what <see cref="IPasskeyHandler{TUser}"/> reads, and it would otherwise derive the
    /// relying-party id from the request host, minting credentials that work on one name and fail
    /// silently on another. The fixed policy on the same options type is
    /// <c>IdentityConfiguration.ConfigurePasskeys</c>, registered beside the rest of Identity's.
    /// </para>
    /// </remarks>
    public static AuthenticationBuilder AddPasskeyAuthentication(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // One per process: the ceremonies this server has issued and not yet seen answered, and the
        // cookie that carries each one between its two requests. Both ceremonies - the sign-in the
        // scheme runs and the registration the Manage page runs - go through the same pair.
        builder.Services.AddSingleton<PasskeyCeremonyLedger>();
        builder.Services.AddSingleton<PasskeyCeremonies>();

        builder.Services.AddOptions<PasskeyAuthenticationOptions>(Schemes.Passkey)
               .Configure<IOptions<Middleware.SecurityOptions>>((options, security) =>
               {
                   options.ServerDomain = security.Value.PasskeyServerDomain;
               });

        builder.Services.AddOptions<IdentityPasskeyOptions>()
               .Configure<IOptionsMonitor<PasskeyAuthenticationOptions>>((engine, schemes) =>
               {
                   PasskeyAuthenticationOptions scheme = schemes.Get(Schemes.Passkey);

                   engine.ServerDomain = scheme.IsConfigured ? scheme.ServerDomain!.Trim() : null;
                   engine.AuthenticatorTimeout = scheme.CeremonyLifetime;
               });

        builder.AddScheme<PasskeyAuthenticationOptions, PasskeyAuthenticationHandler>(Schemes.Passkey,
                                                                                      options => { });

        return builder;
    }

    public static AuthenticationBuilder AddPrusaConnectPrinterAuthentication(this AuthenticationBuilder builder)
    {
        builder.AddScheme<PrusaConnectAuthenticationSchemeOptions, PrusaConnectPrinterAuthenticationHandler>(
            Schemes.PrusaConnectPrinter,
            options => { });

        return builder;
    }

    /// <summary>
    /// Registers the personal access token scheme. It authenticates <c>/api/v1</c> alongside the
    /// sign-in cookie rather than replacing it — see <c>Authorisation.Policies.Api</c>.
    /// </summary>
    public static AuthenticationBuilder AddApiTokenAuthentication(this AuthenticationBuilder builder)
    {
        builder.AddScheme<ApiTokenAuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(Schemes.ApiToken, options => { });

        return builder;
    }

    /// <summary>
    /// Registers the <c>X-Api-Key</c> scheme: the same personal access tokens, in the header
    /// PrusaSlicer's print-host client sends. Reaching an endpoint takes a policy naming it — see
    /// <c>Authorisation.Policies.Compat</c>, which is the only one that does.
    /// </summary>
    public static AuthenticationBuilder AddXApiKeyAuthentication(this AuthenticationBuilder builder)
    {
        builder.AddScheme<ApiTokenAuthenticationSchemeOptions, XApiKeyAuthenticationHandler>(Schemes.XApiKey, options => { });

        return builder;
    }

    /// <summary>
    /// Binds <see cref="OidcOptions"/> from configuration and registers the external OpenID Connect
    /// scheme — but <b>only if a provider is actually configured</b>. Safe to call unconditionally,
    /// which is the point: it belongs in the chain beside the others rather than behind an <c>if</c>
    /// in <c>Program</c>.
    /// </summary>
    /// <param name="builder">The authentication builder to register the scheme on.</param>
    /// <param name="configuration">Configuration to read <see cref="OidcOptions.SectionName"/> from.</param>
    /// <remarks>
    /// <para>
    /// <b>The section is both bound into the container and read here, and it has to be both.</b>
    /// <see cref="OidcOptions"/> is injected by request-time code — <c>Account/ExternalLogin</c> reads
    /// <see cref="OidcOptions.AllowInviteMatchByEmail"/> on every callback — so the container needs the
    /// registration. But <i>whether a scheme exists at all</i> is settled while the pipeline is still
    /// being built, long before anything can resolve an <c>IOptions&lt;T&gt;</c>, so that one question
    /// is answered from a locally bound copy.
    /// </para>
    /// <para>
    /// <b>Registering an unconfigured handler is the thing this avoids.</b> A scheme with no authority
    /// still appears in <c>GetExternalAuthenticationSchemesAsync</c>, and every guard in the codebase
    /// reads a non-empty result as "there is an external provider" — the login page, the username
    /// menu's <c>ExternalLogins</c> entry, <c>ExternalLogin</c>'s own scheme check. Each would then
    /// offer a sign-in that cannot work.
    /// </para>
    /// <para>
    /// <b><see cref="Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions.MapInboundClaims"/>
    /// is off.</b> Microsoft's default rewrites <c>name</c> to
    /// its WS-Federation schema URI, and this codebase deliberately reads the short JWT names —
    /// <c>JwtClaimTypes.Email</c> in <c>Account/ExternalLogin</c>,
    /// <c>JwtClaimTypes.Name</c> in <c>ConnectPrinterAuthentication</c>. Mapped inbound claims do not
    /// fail; they silently stop matching, and the account-creation path then sees no address at all.
    /// The rule is that mapping is off <i>consistently</i> — mixing a mapped handler with an unmapped
    /// one is the failure this prevents.
    /// </para>
    /// <para>
    /// <b>It is set here <i>and</i> as a static clear in <c>Program</c> — and the static one is what
    /// actually carries the rule.</b> Measured rather than reasoned, by mutation: with
    /// <c>JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear()</c> in place, flipping this line to
    /// <c>true</c> changes nothing and all four dex tests still pass, because mapping through an empty
    /// map is a no-op. Remove the static clear as well and
    /// <c>AnInviteIsClaimedByAVerifiedAddressWhenTheOptionIsOn</c> fails — so the suite can see claim
    /// mapping, and what it is seeing is the static clear.
    /// </para>
    /// <para>
    /// <b>Which makes this line defence in depth that no test can catch the removal of</b>, and it is
    /// kept deliberately anyway: the static clear is process-wide state set three hundred lines away,
    /// and a handler that states its own rule survives somebody deciding that line looks like a
    /// leftover. The static one protects a handler registered later by somebody who does not know the
    /// rule; this one tells the person reading this registration what the rule is.
    /// </para>
    /// <para>
    /// <b>Signs in to <c>IdentityConstants.ExternalScheme</c></b>, not to the application cookie. That
    /// is what makes <c>SignInManager.GetExternalLoginInfoAsync</c> find the principal in the callback,
    /// and it is what keeps a provider sign-in from being an application sign-in until
    /// <c>Account/ExternalLogin</c> has decided whether the person may have an account at all.
    /// </para>
    /// </remarks>
    public static AuthenticationBuilder AddOidcAuthentication(this AuthenticationBuilder builder,
                                                              IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(OidcOptions.SectionName);

        builder.Services.Configure<OidcOptions>(section);

        OidcOptions oidc = new();
        section.Bind(oidc);

        if (!oidc.IsConfigured)
        {
            return builder;
        }

        builder.AddOpenIdConnect(Schemes.ExternalOidc, oidc.DisplayName, options =>
        {
            options.Authority = oidc.Authority;
            options.ClientId = oidc.ClientId;
            options.ClientSecret = oidc.ClientSecret;
            options.CallbackPath = oidc.CallbackPath;
            options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;

            options.SignInScheme = IdentityConstants.ExternalScheme;

            // Authorisation code with PKCE. The secret makes this a confidential client; PKCE is
            // alongside it rather than instead of it, and costs nothing to send.
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;

            // Nothing here needs a provider access token afterwards - the account is ours once it
            // exists - and a stored token is a credential to look after for no gain.
            options.SaveTokens = false;

            // The address is what the invite gate matches on, and an id token is not obliged to carry
            // one. Asking userinfo as well is the difference between a provider that works and a
            // provider that authenticates people this deployment then refuses.
            options.GetClaimsFromUserInfoEndpoint = true;

            options.Scope.Clear();
            options.Scope.Add(OidcConstants.StandardScopes.OpenId);
            options.Scope.Add(OidcConstants.StandardScopes.Profile);
            options.Scope.Add(OidcConstants.StandardScopes.Email);

            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = JwtClaimTypes.Name;
            options.TokenValidationParameters.RoleClaimType = JwtClaimTypes.Role;
        });

        return builder;
    }
}
