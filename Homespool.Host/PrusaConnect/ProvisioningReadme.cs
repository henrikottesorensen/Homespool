using System;

using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Writes the instructions that travel with a provisioning bundle.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bundle is opened somewhere the web page is not.</b> Someone downloads it, walks to a printer
/// with a USB stick, and by then everything the page said is gone — including the two things most
/// likely to matter: that the files belong at the root of the stick, and that <c>custom_cert</c> takes
/// the printer away from Prusa Connect until it is put back.
/// </para>
/// <para>
/// So this repeats what the ini's comments say rather than assuming either will be read. The comments
/// survive on the stick and are seen by whoever opens the file; this is seen by whoever opens the zip.
/// Different people, often.
/// </para>
/// </remarks>
public static class ProvisioningReadme
{
    /// <summary>What the file is called in the zip.</summary>
    public const string FileName = "README.Bundle.md";

    /// <summary>
    /// The instructions, filled in for this bundle.
    /// </summary>
    /// <param name="options">Supplies the port and whether TLS is in use.</param>
    /// <param name="hostname">The address written into the ini — what this printer will connect to.</param>
    /// <param name="printerName">The printer this was provisioned for, or null if it was left unnamed.</param>
    /// <param name="localiser">Reads the document in the culture of whoever asked for the bundle.</param>
    /// <remarks>
    /// <b>Localised per block rather than per sentence.</b> A paragraph is the smallest unit a
    /// translator can rearrange freely; splitting further would produce the before/after keys this
    /// project has spent two rounds removing. Markdown structure, code spans and the printer's own
    /// menu paths stay here rather than travelling into the resources.
    /// </remarks>
    public static string Build(PrusaConnectOptions options,
                               string hostname,
                               string? printerName,
                               IStringLocalizer<SharedResource> localiser)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(localiser);

        string forPrinter = string.IsNullOrWhiteSpace(printerName) ?
            localiser["Readme_APrinter"].Value :
            $"**{printerName.Trim()}**";

        string certificateStep = options.PrinterTls ?
            $"""
            ### {localiser["Readme_Step3TlsHeading"].Value}

            {localiser["Readme_Step3TlsBody"].Value}
            """ :
            $"""
            ### {localiser["Readme_Step3PlainHeading"].Value}

            {localiser["Readme_Step3PlainBody"].Value}
            """;

        string afterwards = options.PrinterTls ?
            $"""
            ## {localiser["Readme_NoConnectHeading"].Value}

            {localiser["Readme_NoConnectBody"].Value}
            """ :
            string.Empty;

        string certificateRow = options.PrinterTls ?
            $"| `{ProvisioningBundleBuilder.AuthorityFileName}` | {localiser["Readme_RowDer"].Value} |" :
            string.Empty;

        string connection = options.PrinterTls ?
            localiser["Readme_ConnectionTls"].Value :
            localiser["Readme_ConnectionPlain"].Value;

        return $"""
                # {localiser["Readme_Title", forPrinter].Value}

                {localiser["Readme_Intro"].Value}

                ## {localiser["Readme_ContentsHeading"].Value}

                | {localiser["Readme_ColumnFile"].Value} | {localiser["Readme_ColumnWhat"].Value} |
                |---|---|
                | `{ConnectIni.FileName}` | {localiser["Readme_RowIni"].Value} |
                {certificateRow}
                | `{FileName}` | {localiser["Readme_RowReadme"].Value} |

                ## {localiser["Readme_OnThePrinterHeading"].Value}

                ### {localiser["Readme_Step1Heading"].Value}

                {localiser["Readme_Step1Body"].Value}

                ### {localiser["Readme_Step2Heading"].Value}

                {localiser["Readme_Step2Body"].Value}

                {certificateStep}

                ### {localiser["Readme_Step4Heading"].Value}

                {localiser["Readme_Step4Body", LongMenuPath, ShortMenuPath].Value}

                ### {localiser["Readme_Step5Heading"].Value}

                {localiser["Readme_Step5Body", localiser["Printers_Title"].Value].Value}

                ## {localiser["Readme_DeleteHeading"].Value}

                {localiser["Readme_DeleteBody"].Value}

                {afterwards}

                ## {localiser["Readme_TroubleHeading"].Value}

                {localiser["Readme_TroubleConfigFailed"].Value}

                {localiser["Readme_TroubleTls"].Value}

                {localiser["Readme_TroubleNothing", hostname, options.PrinterPort].Value}

                {localiser["Readme_TroubleStopped"].Value}

                {localiser["Readme_TroubleLostDownload", localiser["Printers_ReissueToken"].Value].Value}

                ## {localiser["Readme_PointsAtHeading"].Value}

                | | |
                |---|---|
                | {localiser["Readme_RowServerAddress"].Value} | `{hostname}` |
                | {localiser["Readme_RowPort"].Value} | `{options.PrinterPort}` |
                | {localiser["Readme_RowConnection"].Value} | {connection} |

                """;
    }

    /// <summary>The printer's own menu paths, in firmware's wording rather than ours.</summary>
    /// <remarks>
    /// Kept out of the resources for the same reason as <see cref="ConnectIni"/>'s: they name
    /// something on another screen, spelled the way that screen spells it. See
    /// <c>notes/localisation.md</c> on why that is right for Danish by accident rather than by rule.
    /// </remarks>
    private const string LongMenuPath = "Settings → Network → Prusa Connect → Load Settings";

    /// <inheritdoc cref="LongMenuPath"/>
    private const string ShortMenuPath = "Prusa Connect → Load Settings";
}
