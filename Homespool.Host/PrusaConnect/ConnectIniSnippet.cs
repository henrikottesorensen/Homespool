namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Builds the <c>[service::connect]</c> section of a <c>prusa_printer_settings.ini</c> for USB-key
/// provisioning (protocol-reference.md, "The .ini path is a second enrolment channel"). Deliberately
/// only this one section: the rest of the file - <c>[network]</c>, <c>[service::local]</c>, and any
/// wifi credentials - is the operator's own and is never generated here, since this server never has
/// and never should have wifi credentials.
/// </summary>
/// <remarks>
/// Key names and casing verified against <c>connect_ini_handler</c> in
/// <c>Prusa-Firmware-Buddy/src/connect/marlin_printer.cpp</c>: <c>hostname</c>, <c>port</c>, <c>tls</c>
/// (accepts <c>1</c>/<c>0</c> or case-insensitive <c>true</c>/<c>false</c> - <c>True</c>/<c>False</c>
/// is what a real exported ini uses, so that's what this emits), <c>token</c> (silently rejected, not
/// truncated, past <see cref="TokenService.PrinterTokenLength"/> bytes).
/// </remarks>
public static class ConnectIniSnippet
{
    public static string Build(PrusaConnectOptions options, string token)
    {
        string tls = options.PrinterTls ? "True" : "False";

        return $"""
                [service::connect]
                hostname = {options.PrinterHost}
                port = {options.PrinterPort}
                tls = {tls}
                token = {token}
                """;
    }
}
