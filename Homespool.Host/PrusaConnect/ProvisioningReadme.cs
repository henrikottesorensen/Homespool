using System;

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
    public static string Build(PrusaConnectOptions options, string hostname, string? printerName)
    {
        ArgumentNullException.ThrowIfNull(options);

        string forPrinter = string.IsNullOrWhiteSpace(printerName)
            ? "a printer"
            : $"**{printerName.Trim()}**";

        string certificateStep = options.PrinterTls
            ? """
              ### 3. Both files, or neither

              `connect.der` is the certificate your printer will check this server against, and the ini
              tells it to use that file and nothing else. One without the other does not work: the ini
              alone leaves the printer with no way to verify the server, and the certificate alone is
              never read.
              """
            : """
              ### 3. This bundle has no certificate, deliberately

              This server is configured to talk to printers over plain HTTP, so there is nothing for the
              printer to verify and no `connect.der` in the zip. The token below crosses your network in
              clear text. That is a setting for testing, not for a printer you rely on.
              """;

        string afterwards = options.PrinterTls
            ? """
              ## While this bundle is loaded, the printer cannot use Prusa Connect

              `custom_cert = 1` **replaces** the certificates your printer shipped with, rather than
              adding to them. That is what lets it trust your server, and it is also why it can no
              longer verify Prusa's. To undo it, load an ini with `custom_cert = 0` and the Prusa
              Connect settings you want back.
              """
            : string.Empty;

        return $"""
                # Provisioning bundle for {forPrinter}

                This zip sets a Prusa printer up to talk to **your** Homespool server instead of Prusa
                Connect. It was generated for one printer and one server; it is not reusable and not
                transferable.

                ## What is in here

                | file | what it is |
                |---|---|
                | `prusa_printer_settings.ini` | the settings your printer reads, including a token that enrols it |
                {(options.PrinterTls ? "| `connect.der` | the certificate authority your printer will check this server against |" : string.Empty)}
                | `README.Bundle.md` | this file, which the printer ignores |

                ## Putting it on the printer

                ### 1. Unzip onto a USB stick

                Copy the files to the **top level** of the stick — not into a folder. If you end up with
                a folder on the stick, the printer will not find anything and will not tell you why.

                A stick that already works with your printer is the right stick. If you are formatting a
                new one, use FAT32.

                ### 2. Put the stick in the printer

                Any USB port it normally uses.

                {certificateStep}

                ### 4. Load the settings

                On the printer: **Settings → Network → Prusa Connect → Load Settings**, or on some
                firmware simply **Prusa Connect → Load Settings**. The printer reads the file, restarts
                its connection, and within a few seconds should show as online.

                ### 5. Check the server

                The printer appears under **Printers** in Homespool. It enrols itself on first contact,
                so there is nothing else to press.

                ## When it has worked, delete the file

                `prusa_printer_settings.ini` contains a token that is this printer's password to your
                server. Once the printer has connected, delete it from the stick. Nothing needs it
                again — the printer has kept what it needs.

                {afterwards}

                ## If it does not work

                **The printer says the config failed to load.** Something edited the file. It is
                sensitive about comment characters (`#` only, never `;`) and about being complete —
                every key has to be there. Re-download the bundle rather than repairing it by hand;
                that is the entire reason it is generated.

                **The printer says there is a TLS or certificate error.** Three usual causes, in order
                of likelihood: `connect.der` was not copied alongside the ini; the server's certificate
                has been reissued since this bundle was downloaded, so download a fresh one; or the
                printer's clock is far enough out to reject the certificate, which fixes itself once it
                has been on a network with internet access for a minute.

                **Nothing happens at all.** Check the printer can reach `{hostname}` on port
                `{options.PrinterPort}` from where it sits — a different wifi network or a firewall
                between the two is the usual answer.

                **It worked for weeks and then stopped.** Most likely this server's address changed.
                Your router hands it out on a lease, and a reboot or a busy network can move it. Give
                this server a **static lease** — sometimes called a DHCP reservation, or address
                binding — in your router's settings. It takes a minute and it is the one change that
                stops this recurring; without it, every move means walking a stick to every printer
                again.

                **You lost the download before the printer connected.** Nothing is broken. In Homespool,
                use **Reissue USB token** on the printer's row and download a new bundle; the old token
                stops working the moment you do.

                ## What this bundle points at

                | | |
                |---|---|
                | server address | `{hostname}` |
                | port | `{options.PrinterPort}` |
                | connection | {(options.PrinterTls
                    ? "TLS — the same encryption your browser calls HTTPS. This is the `tls = True` line in the ini."
                    : "plain HTTP, **not encrypted**. This is the `tls = False` line in the ini, and it is for testing only.")} |

                """;
    }
}
