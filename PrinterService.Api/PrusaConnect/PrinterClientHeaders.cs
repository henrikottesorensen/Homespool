using System;
using System.Linq;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace PrinterService.Api.PrusaConnect;

/// <summary>
/// The printer-identifying headers a client may attach to a request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every property is optional, and reading these headers never throws.</b> An earlier version
/// required <c>User-Agent-Printer</c>, <c>User-Agent-Version</c> and <c>Fingerprint</c>, throwing
/// when any was absent — which the register actions caught and turned into a 400. That made
/// enrollment impossible: Buddy sends **no** headers at all on <c>POST /p/register</c> and only
/// <c>Code</c> on the poll, because during registration the printer has no token and its identity
/// is being established rather than asserted. See notes/protocol-reference.md.
/// </para>
/// <para>
/// The Python SDK does send <c>Fingerprint</c> on registration (its <c>make_headers()</c> adds it
/// to every request). So the rule is <b>use it if present, never require it</b>, which satisfies
/// both clients instead of trading one for the other.
/// </para>
/// <para>
/// Endpoints that genuinely require identity are protected by
/// <c>PrusaConnectPrinterAuthenticationHandler</c>, which does its own header checks. This type is
/// a tolerant reader, not a validator.
/// </para>
/// </remarks>
public class PrinterClientHeaders
{
    public string? Printer { get; }

    public string? FirmwareVersion { get; }

    public string? FingerPrint { get; }

    public string? Token { get; }

    /// <summary>
    /// The registration code, from the <c>Code</c> header.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>Temporary-Code</c>.</b> Prusa's server emits both headers with the same value, but
    /// neither reference client ever sends or reads <c>Temporary-Code</c> — Buddy's
    /// <c>PollRequest</c> and the SDK's <c>Register.send()</c> both set <c>Code</c>. Reading the
    /// wrong one meant every poll was rejected. We still emit both on the response, to mirror the
    /// server we are emulating.
    /// </remarks>
    public string? Code { get; }

    public PrinterClientHeaders(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Printer = Single(request, Headers.UserAgentPrinter);
        FirmwareVersion = Single(request, Headers.UserAgentVersion);
        FingerPrint = Single(request, Headers.Fingerprint);
        Token = Single(request, Headers.Token);
        Code = Single(request, Headers.Code);
    }

    private static string? Single(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out StringValues values) && values.Count == 1
            ? values.Single()
            : null;
}
