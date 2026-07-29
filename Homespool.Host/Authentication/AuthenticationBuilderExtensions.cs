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
}
