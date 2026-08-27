using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Options;

using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Printing;

/// <summary>
/// Telling a printer to fetch one of a user's files: mint a transfer token, offer the bytes under
/// it, and send the command.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exists because two callers need it</b> - the API endpoint and the Files page - and the rule
/// worth having in one place is not the happy path but the cleanup: <b>a send that did not take must
/// leave no offer behind</b>. That applies to a throw and equally to a printer answering
/// <c>Rejected</c>, which is easy to remember in the first implementation and easy to forget in the
/// second. An offer that outlives its command is a pinned file descriptor waiting an hour for the
/// sweep.
/// </para>
/// <para>
/// What is deliberately <i>not</i> here: resolving the printer, checking the file exists, and the
/// 4 GiB ceiling. Those are preconditions each caller already has in hand and phrases its own way -
/// a status code on one side, a sentence on a page on the other.
/// </para>
/// </remarks>
public class PrintFileSender
{
    /// <summary>
    /// Bytes of randomness in a transfer token. 21 rather than 20 because base64url carries three
    /// bytes per four characters, so 21 encodes to exactly 28 - filling firmware's hash buffer
    /// (<see cref="StartConnectDownload.MaxHashLength"/>) with nothing left over and no padding.
    /// </summary>
    /// <remarks>
    /// Unguessable is not load-bearing: the token is only meaningful to the printer that was just
    /// told to use it, and ownership is enforced before one is ever minted. It is random because
    /// there is no reason for it to be anything else, and 168 bits is what the space happened to be.
    /// </remarks>
    private const int TransferTokenBytes = 21;

    private readonly ITransferOffers _offers;
    private readonly EncryptedTransferOffers _encrypted;
    private readonly PrinterCommandService _commands;
    private readonly IOptionsMonitor<PrusaConnectOptions> _options;

    public PrintFileSender(ITransferOffers offers,
                           EncryptedTransferOffers encrypted,
                           PrinterCommandService commands,
                           IOptionsMonitor<PrusaConnectOptions> options)
    {
        _offers = offers;
        _encrypted = encrypted;
        _commands = commands;
        _options = options;
    }

    /// <summary>
    /// Offers <paramref name="file"/> and tells <paramref name="printer"/> to come and get it,
    /// returning whatever the printer answered.
    /// </summary>
    /// <exception cref="PrintFileUnreadableException">
    /// The file could not be opened, which means it was deleted between being found and being
    /// offered - a delete racing this send rather than anything the caller did wrong.
    /// </exception>
    /// <remarks>
    /// Returns as soon as the printer accepts the command, which is not when the transfer finishes:
    /// it then pulls the bytes at its own pace over the same WebSocket, and a full-size model takes
    /// minutes. Watch for <c>TRANSFER_FINISHED</c>, or the transfer fields in telemetry.
    /// </remarks>
    public async Task<FileSendResult> SendAsync(Printer printer,
                                                StoredFile file,
                                                Caller caller,
                                                CancellationToken cancellationToken)
    {
        // Which download the printer can perform is a property of its connection, not of the file
        // or the caller. Same gate as the send that follows - permission and connectedness - so a
        // printer that would be refused a send is refused here, with the same exceptions.
        bool inline = await _commands.CanStreamChunksAsync(printer.Id, caller, Capability.Print, cancellationToken);

        // Three ways to hand over a file, and only the first two are about the connection. The third
        // is about the client: the Python Connect SDK has no encrypted download at all, so offering
        // it one is a command it never answers and a send that times out. It fetches the same
        // START_CONNECT_DOWNLOAD the socket transfer uses, over plain HTTP from the printer listener
        // - which is why this branch sends exactly that and only the fetch differs.
        bool encrypted = !inline
                         && await _commands.CanDecryptDownloadsAsync(printer.Id, caller, Capability.Print, cancellationToken);

        // Which command went out is reported rather than left to be inferred: the caller cannot see
        // this branch, and a refusal that names the wrong command sends somebody to the wrong code.
        return encrypted ?
            new FileSendResult(PrusaConnect.Commands.StartEncryptedDownload.Wire,
                               await SendEncryptedAsync(printer, file, caller, cancellationToken)) :
            new FileSendResult(PrusaConnect.Commands.StartConnectDownload.Wire,
                               await SendInlineAsync(printer, file, caller, cancellationToken));
    }

    /// <summary>
    /// The inline transfer: the printer pulls chunks over its Connect socket, from an offer keyed
    /// by a random token it quotes back.
    /// </summary>
    private async Task<CommandOutcome?> SendInlineAsync(Printer printer,
                                                        StoredFile file,
                                                        Caller caller,
                                                        CancellationToken cancellationToken)
    {
        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TransferTokenBytes));

        // Opening the file is what pins these bytes for the whole transfer - see ITransferOffers.
        if (!_offers.Offer(token, file.Path))
        {
            throw new PrintFileUnreadableException(file.FileName);
        }

        StartConnectDownload command = new()
        {
            Path = file.PrinterPath,
            Hash = token,
            TeamId = (ulong)printer.TeamId,
            OriginalSize = file.Length,
        };

        return await SendAndCleanUpAsync(printer, command, caller, () => _offers.Revoke(token), cancellationToken);
    }

    /// <summary>
    /// The encrypted download: the printer fetches AES-CTR ciphertext over a plain HTTP connection
    /// it opens itself, from <c>/f/&lt;iv&gt;/raw</c> on the transfer listener. What a printer on
    /// the pre-websocket transport does, since it has no socket to pull chunks over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Key and IV are minted fresh, per transfer, at random.</b> CTR under a repeated (key, IV)
    /// reuses a keystream, and two files under one keystream leak their XOR - for gcode with a shared
    /// slicer preamble, most of the smaller file. Deriving the IV from the file would give stable
    /// URLs and exactly that reuse; <see cref="TransferCipher"/>'s remarks call it the one fatal
    /// shortcut. So there is no content-addressing here at any price.
    /// </para>
    /// <para>
    /// The offer is keyed by the IV's hex, because that is what the printer's request carries and
    /// what <c>EncryptedTransferController</c> looks up. Two stores share the key: the offer store
    /// pins the bytes, <see cref="EncryptedTransferOffers"/> holds the AES key beside them, and both
    /// are cleaned up together on every path a send can fail by.
    /// </para>
    /// <para>
    /// <see cref="StartEncryptedDownload.Port"/> is always sent. Firmware would otherwise fetch from
    /// the port in its enrolled config, rewriting 443-with-TLS to 80 - and the transfer listener is
    /// on neither. <see cref="PrusaConnectOptions.TransferPort"/> is the public number.
    /// </para>
    /// </remarks>
    private async Task<CommandOutcome?> SendEncryptedAsync(Printer printer,
                                                           StoredFile file,
                                                           Caller caller,
                                                           CancellationToken cancellationToken)
    {
        byte[] key = RandomNumberGenerator.GetBytes(TransferCipher.KeyLength);
        byte[] iv = RandomNumberGenerator.GetBytes(TransferCipher.IvLength);
        string ivHex = Convert.ToHexStringLower(iv);

        try
        {
            if (!_offers.Offer(ivHex, file.Path))
            {
                throw new PrintFileUnreadableException(file.FileName);
            }

            _encrypted.Register(ivHex, key, ivHex);

            // The command takes its own copies. It may be rendered long after this method returns -
            // on the HTTP transport it is parked and encoded when the printer next polls - so it must
            // not share an array with anything zeroed below.
            StartEncryptedDownload command = new()
            {
                Path = file.PrinterPath,
                Key = (byte[])key.Clone(),
                Iv = (byte[])iv.Clone(),
                OriginalSize = file.Length,
                Port = checked((ushort)_options.CurrentValue.TransferPort),
            };

            return await SendAndCleanUpAsync(printer,
                                             command,
                                             caller,
                                             () =>
                                             {
                                                 _encrypted.Revoke(ivHex);
                                                 _offers.Revoke(ivHex);
                                             },
                                             cancellationToken);
        }
        finally
        {
            // Only this method's copy. The store holds its own and zeroes it on revoke; the command
            // holds its own and lives as long as the actor keeps it.
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Sends the command, and runs <paramref name="revoke"/> on every path where the printer will
    /// never come for the bytes: a throw, or an answer of <c>Rejected</c>/<c>Failed</c>. The rule
    /// this class exists to keep in one place.
    /// </summary>
    private async Task<CommandOutcome?> SendAndCleanUpAsync(Printer printer,
                                                            ISendableCommand command,
                                                            Caller caller,
                                                            Action revoke,
                                                            CancellationToken cancellationToken)
    {
        CommandOutcome? outcome;

        try
        {
            outcome = await _commands.SendCommandAsync(printer.Id, command, caller, cancellationToken);
        }
        catch
        {
            revoke();

            throw;
        }

        if (outcome?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            // The printer will never ask for these bytes now, so the offer is dead weight holding a
            // descriptor open. Revoking is the same cleanup as the throw above, for the case that
            // does not look like a failure from here.
            revoke();
        }

        return outcome;
    }
}
