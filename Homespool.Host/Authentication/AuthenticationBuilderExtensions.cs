using Microsoft.AspNetCore.Authentication;

namespace Homespool.Host.Authentication;

public static class AuthenticationBuilderExtensions
{
    public static AuthenticationBuilder AddPrusaConnectPrinterAuthentication(this AuthenticationBuilder builder)
    {
        builder.AddScheme<PrusaConnectAuthenticationSchemeOptions, PrusaConnectPrinterAuthenticationHandler>(
            Schemes.PrusaConnectPrinter,
            options =>
            {
            });

        return builder;
    }

    /// <summary>
    /// Registers the personal access token scheme. It authenticates <c>/api/v1</c> alongside the
    /// sign-in cookie rather than replacing it — see <c>Authorisation.Policies.Api</c>.
    /// </summary>
    public static AuthenticationBuilder AddApiTokenAuthentication(this AuthenticationBuilder builder)
    {
        builder.AddScheme<ApiTokenAuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
            Schemes.ApiToken,
            options =>
            {
            });

        return builder;
    }

    /// <summary>
    /// Registers the <c>X-Api-Key</c> scheme: the same personal access tokens, in the header
    /// PrusaSlicer's print-host client sends. Reaching an endpoint takes a policy naming it — see
    /// <c>Authorisation.Policies.Compat</c>, which is the only one that does.
    /// </summary>
    public static AuthenticationBuilder AddXApiKeyAuthentication(this AuthenticationBuilder builder)
    {
        builder.AddScheme<ApiTokenAuthenticationSchemeOptions, XApiKeyAuthenticationHandler>(
            Schemes.XApiKey,
            options =>
            {
            });

        return builder;
    }
}
