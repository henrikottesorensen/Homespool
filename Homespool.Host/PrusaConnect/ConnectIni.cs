using System;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Writes the <c>[service::connect]</c> section of a <c>prusa_printer_settings.ini</c> — as a snippet
/// to read on screen, or as the whole file that goes into a provisioning bundle
/// (protocol-reference.md, "The .ini path is a second enrolment channel").
/// </summary>
/// <remarks>
/// <para>
/// Deliberately only this one section: the rest of the file — <c>[network]</c>,
/// <c>[service::local]</c>, and any wifi credentials — is the operator's own and is never generated
/// here, since this server never has and never should have wifi credentials.
/// </para>
/// <para>
/// Key names and casing verified against <c>connect_ini_handler</c> in
/// <c>Prusa-Firmware-Buddy/src/connect/marlin_printer.cpp</c>: <c>hostname</c>, <c>port</c>, <c>tls</c>
/// (accepts <c>1</c>/<c>0</c> or case-insensitive <c>true</c>/<c>false</c> — <c>True</c>/<c>False</c>
/// is what a real exported ini uses, so that's what this emits), <c>token</c> (silently rejected, not
/// truncated, past <see cref="TokenService.PrinterTokenLength"/> bytes).
/// </para>
/// <para>
/// <b>Every key, every time.</b> An omitted key is not left alone by the firmware — it is reset to its
/// struct default, and <c>token</c>'s default is empty, which de-enrols the printer. That is one of the
/// four hand-assembly failures this path exists to make unrepresentable
/// (<c>notes/usb-provisioning-bundle.md</c>).
/// </para>
/// </remarks>
public static class ConnectIni
{
    /// <summary>
    /// What the printer expects this file to be called on the stick. Anything else is ignored without
    /// comment.
    /// </summary>
    public const string FileName = "prusa_printer_settings.ini";

    /// <summary>
    /// The section on its own, to paste into an existing ini — for someone who would rather read what
    /// they are about to do than trust a zip.
    /// </summary>
    /// <param name="options">Supplies the port and whether TLS is in use.</param>
    /// <param name="hostname">The address this printer should use: one of the names in the certificate.</param>
    /// <param name="token">The provisioning token, which is what makes this a credential.</param>
    public static string BuildSnippet(PrusaConnectOptions options, string hostname, string token)
    {
        ArgumentNullException.ThrowIfNull(options);

        // custom_cert follows tls and is never a separate question: the firmware carries no public CA
        // bundle at all - not even ISRG Root X1 - so its own certificates are useless against any
        // server but Prusa's. A Let's Encrypt deployment needs a DER shipped exactly as a private one
        // does, which is the opposite of what an operator expects.
        return $"""
                [service::connect]
                hostname = {hostname}
                port = {options.PrinterPort}
                tls = {(options.PrinterTls ? "True" : "False")}
                custom_cert = {(options.PrinterTls ? "1" : "0")}
                token = {token}
                """;
    }

    /// <summary>
    /// The whole file, as it goes onto the stick: the section above wrapped in the comments that have
    /// to travel with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The comments are the deliverable as much as the keys are.</b> <c>custom_cert</c> is
    /// <i>exclusive</i> — it replaces the firmware's trust store rather than adding to it — so a
    /// printer given this file can no longer validate Prusa Connect until someone puts it back. That
    /// warning is needed at the moment something fails, which is at the printer with a stick in hand,
    /// not on the web page it was downloaded from.
    /// </para>
    /// <para>
    /// <b><c>#</c>, never <c>;</c>.</b> Buddy sets <c>INI_START_COMMENT_PREFIXES "#"</c>
    /// (<c>ini.h:88</c>) with <c>INI_ALLOW_NO_VALUE 0</c>, so a <c>;</c> line is not an ignored comment
    /// — it is a parse error that fails the whole file as <i>"Failed to load config"</i>. That cost an
    /// afternoon once, which is exactly why this file is generated rather than described.
    /// </para>
    /// </remarks>
    public static string BuildFile(PrusaConnectOptions options, string hostname, string token)
    {
        ArgumentNullException.ThrowIfNull(options);

        string transportNote = options.PrinterTls
            ? """
              # custom_cert = 1 makes connect.der, beside this file, the printer's ENTIRE trust store -
              # replacing the certificates it shipped with rather than adding to them. While that is
              # set, this printer cannot talk to Prusa Connect. Set custom_cert = 0 to undo it.
              """
            : """
              # tls = False: this printer's token crosses the network in clear, and so does everything
              # it says afterwards. That is a setting for reading the wire on a network you control.
              """;

        return $"""
                # Homespool provisioning. Copy this file to the root of a USB stick - not into a folder,
                # or the printer will not find it - and load it from the printer's own menu:
                # Prusa Connect -> Load Settings.
                #
                # This file carries a token that enrols this printer. Treat it as you would a password,
                # and delete it from the stick once the printer has connected.
                #
                {transportNote}
                #
                # Only [service::connect] is written here. Your [network] section and wifi credentials
                # are yours - this server has never had them.

                {BuildSnippet(options, hostname, token)}

                """;
    }
}
