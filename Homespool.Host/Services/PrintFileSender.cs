using System.Buffers.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Services;

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
    private readonly PrinterCommandService _commands;

    public PrintFileSender(ITransferOffers offers, PrinterCommandService commands)
    {
        _offers = offers;
        _commands = commands;
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
    public async Task<CommandOutcome?> SendAsync(Printer printer,
                                                 StoredFile file,
                                                 long userId,
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

        CommandOutcome? outcome;

        try
        {
            outcome = await _commands.SendCommandAsync(printer.Id, command, userId, cancellationToken);
        }
        catch
        {
            _offers.Revoke(token);

            throw;
        }

        if (outcome?.EventType is Events.Rejected or Events.Failed)
        {
            // The printer will never ask for these bytes now, so the offer is dead weight holding a
            // descriptor open. Revoking is the same cleanup as the throw above, for the case that
            // does not look like a failure from here.
            _offers.Revoke(token);
        }

        return outcome;
    }
}
