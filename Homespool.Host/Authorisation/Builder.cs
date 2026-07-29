using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Homespool.Host.Authorisation;

public static class Builder
{
    public static AuthorizationOptions Build()
    {
        AuthorizationOptions options = new();
        Build(options);

        return options;
    }

    public static void Build(AuthorizationOptions options)
    {
        options.AddPolicy(Policies.PrusaConnectPrinter,
             policy => policy.RequireAuthenticatedUser()
                                          .AddAuthenticationSchemes(Authentication.Schemes.PrusaConnectPrinter));

        // Both schemes, not one: the web UI calls these endpoints with its cookie and scripts call
        // them with a token, so the token scheme is added to the API rather than substituted for the
        // cookie. Naming the schemes explicitly also stops the printer scheme from ever answering
        // here - without a list, the default scheme alone applies and additions elsewhere leak in.
        options.AddPolicy(Policies.Api,
             policy => policy.RequireAuthenticatedUser()
                                          .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme,
                                                                    Authentication.Schemes.ApiToken));
    }
}
