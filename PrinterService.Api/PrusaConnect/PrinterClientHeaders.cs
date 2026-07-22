using System;
using System.Linq;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace PrinterService.Api.PrusaConnect;

public class PrinterClientHeaders
{
    public string Printer { get; set; }

    public string FirmwareVersion { get; set; }

    public string FingerPrint { get; set; }

    public string? Token { get; set; }
    
    public string? TemporaryCode { get; set; } 

    public PrinterClientHeaders(HttpRequest request)
    {
        request.Headers.TryGetValue(Headers.UserAgentPrinter, out StringValues uaPrinter);
        request.Headers.TryGetValue(Headers.UserAgentVersion, out StringValues uaVersion);
        request.Headers.TryGetValue(Headers.Fingerprint, out StringValues fingerPrint);
        request.Headers.TryGetValue(Headers.Token, out StringValues token);
        request.Headers.TryGetValue(Headers.TemporaryCode, out StringValues code);
        
        Printer = uaPrinter.SingleOrDefault() ?? throw new ArgumentNullException(uaPrinter, $"{Headers.UserAgentPrinter} header missing");
        FirmwareVersion = uaVersion.SingleOrDefault() ?? throw new ArgumentNullException(uaVersion, $"{Headers.UserAgentVersion} header missing");
        FingerPrint = fingerPrint.SingleOrDefault() ?? throw new ArgumentNullException(fingerPrint, $"{Headers.Fingerprint} header missing");

        if (token.Count == 1)
        {
            Token = token.Single();
        }

        if (code.Count == 1)
        {
            TemporaryCode = code.Single();
        }
    }
}
