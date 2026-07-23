using Microsoft.AspNetCore.Authentication;

namespace PrinterService.Host.Authentication;

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
}
