using System;
using System.Collections.Generic;
using System.Text;

using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Writes the <c>[service::connect]</c> section of a <c>prusa_printer_settings.ini</c> — as a snippet
/// to read on screen, or as the whole file that goes into a provisioning bundle. The <c>.ini</c>
/// path is a second enrolment channel alongside the code exchange.
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
/// four hand-assembly failures this path exists to make unrepresentable.
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
    /// <summary>
    /// The whole file, comments and all, for the provisioning bundle.
    /// </summary>
    /// <remarks>
    /// <b>The comments are the only instructions that reach the stick.</b> Whoever opens this file is
    /// standing at a printer, and everything the download page said is behind them - so these are
    /// localised like any other sentence, while the keys, the section names and the printer's own menu
    /// path are not. Firmware parses the first two; the third names a menu in firmware's language
    /// rather than ours.
    /// </remarks>
    /// <param name="options">Supplies the port and whether TLS is in use.</param>
    /// <param name="hostname">The address this printer should use: one of the names in the certificate.</param>
    /// <param name="token">The provisioning token, which is what makes this a credential.</param>
    /// <param name="localiser">Reads the comments in the culture of whoever asked for the bundle.</param>
    public static string BuildFile(PrusaConnectOptions options,
                                   string hostname,
                                   string token,
                                   IStringLocalizer<SharedResource> localiser)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(localiser);

        string transportNote = options.PrinterTls ?
            localiser["Ini_CustomCertNote"].Value :
            localiser["Ini_PlainHttpNote"].Value;

        return $"""
                {Commented(localiser["Ini_HowToLoad"].Value)}
                # {PrinterMenuPath}.
                #
                {Commented(localiser["Ini_TokenIsAPassword"].Value)}
                #
                {Commented(transportNote)}
                #
                {Commented(localiser["Ini_SectionScope"].Value)}

                {BuildSnippet(options, hostname, token)}

                """;
    }

    /// <summary>
    /// The printer's own menu path, which is firmware's wording rather than ours.
    /// </summary>
    /// <remarks>
    /// Left in English for the same reason PrusaSlicer's menu paths are: it names something the reader
    /// will look for on another screen, spelled the way that screen spells it. Whether that is right
    /// depends on whether their firmware speaks their language, which this cannot know.
    /// </remarks>
    private const string PrinterMenuPath = "Prusa Connect -> Load Settings";

    /// <summary>
    /// Wraps a sentence as ini comment lines, wrapped near 96 characters.
    /// </summary>
    /// <remarks>
    /// <b>Wrapped here rather than in the resource</b>, because a translator should be writing
    /// sentences and not counting columns - and Danish runs longer than English, so a hand-wrapped
    /// translation would wrap in the wrong places or not at all.
    /// </remarks>
    private static string Commented(string sentence)
    {
        List<string> lines = [];
        StringBuilder line = new("#");

        foreach (string word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + 1 + word.Length > 96)
            {
                lines.Add(line.ToString());
                line = new StringBuilder("#");
            }

            line.Append(' ').Append(word);
        }

        lines.Add(line.ToString());

        return string.Join("\n", lines);
    }
}
