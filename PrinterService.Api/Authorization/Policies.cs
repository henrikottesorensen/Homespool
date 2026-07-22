using Microsoft.AspNetCore.Authorization;

namespace PrinterService.Api.Authorization;

public static class Policies
{
    public const string PrusaConnectPrinter = nameof(PrusaConnectPrinter);
}
