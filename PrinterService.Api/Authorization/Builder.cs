using Microsoft.AspNetCore.Authorization;

namespace PrinterService.Api.Authorization;

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
        options.AddPolicy(Authorization.Policies.PrusaConnectPrinter, policy => policy.RequireAuthenticatedUser().AddAuthenticationSchemes(Authentication.Schemes.PrusaConnectPrinter));
    }
}
