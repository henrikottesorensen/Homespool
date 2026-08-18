using System;
using System.Buffers;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.Controllers;

/// <summary>
/// Serves an encrypted download: the file a printer on the pre-websocket transport was told to
/// fetch, as AES-CTR ciphertext, over the plain HTTP connection it opens itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract is firmware's, read from <c>download.cpp</c> at v6.2.6, and every part of it is
/// checked on the printer:</b> the request is <c>GET /f/&lt;iv&gt;/raw</c> with a <c>Range</c> header
/// (line 199); the answer must be <c>200</c> for a fetch from zero and <b><c>206</c> for anything
/// else</b> - a resumed download answered <c>200</c> is failed outright (line 234); it must carry
/// <c>Content-Length</c> (line 239) and <c>Content-Encryption-Mode: AES-CTR</c> (line 244), and the
/// body is ciphertext from the requested offset. Nothing about the body is authenticated, which is
/// <see cref="TransferCipher"/>'s malleability caveat and the reason this lives on its own
/// deliberately-plain listener.
/// </para>
/// <para>
/// <b>Anonymous, on purpose.</b> The request presents no fingerprint and no token; the IV in the URL
/// is the capability, minted per transfer and known only to the printer that was told it. There is
/// nothing to authenticate against, and adding a credential would mean inventing one firmware does
/// not send.
/// </para>
/// <para>
/// <b>Range is not optional here.</b> It is how a dropped download resumes, and firmware always sends
/// one. The start must be block-aligned for the cipher's counter to be right - the printer only ever
/// asks for sector-aligned ranges (transfer.cpp:205-206), so an unaligned start is refused as
/// unsatisfiable rather than served as garbage that decrypts to nothing.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
public sealed class EncryptedTransferController : ControllerBase
{
    /// <summary>The header firmware requires on the response, echoing what it sent.</summary>
    public const string ContentEncryptionModeHeader = "Content-Encryption-Mode";

    /// <summary>The only mode firmware still accepts (download.cpp:244).</summary>
    public const string AesCtr = "AES-CTR";

    /// <summary>
    /// Read-and-encrypt granularity. 64 KiB is a whole number of AES blocks and enough that the
    /// per-call overhead of the read and the transform is not what bounds throughput.
    /// </summary>
    private const int BufferSize = 64 * 1024;

    private readonly EncryptedTransferOffers _keys;
    private readonly ITransferContentStore _content;
    private readonly ILogger<EncryptedTransferController> _logger;

    public EncryptedTransferController(EncryptedTransferOffers keys,
                                       ITransferContentStore content,
                                       ILogger<EncryptedTransferController> logger)
    {
        _keys = keys;
        _content = content;
        _logger = logger;
    }

    [HttpGet]
    [Route("/f/{iv}/raw")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status206PartialContent)]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound)]
    public Results<EncryptedTransferController.EncryptedBodyResult, NotFound, StatusCodeResult<Status.RangeNotSatisfiable>> Fetch(string iv)
    {
        // The IV as it appears in the URL is what firmware built from the bytes we sent, lowercase
        // hex (make_enc_url, planner.cpp:191-206). Normalised anyway: the lookup key must match what
        // was registered whatever a client typed.
        string ivHex = iv.ToLowerInvariant();

        EncryptedTransfer? transfer = ivHex.Length == TransferCipher.IvLength * 2 ? _keys.Find(ivHex) : null;

        if (transfer is null || !_content.TryOpen(transfer.OfferToken, out ITransferContent? content))
        {
            // Unknown, revoked, or the bytes are gone. All one answer to the printer, which will
            // report the transfer failed - the same thing it does for any 4xx.
            return TypedResults.NotFound();
        }

        long length = content.Length;

        if (!TryReadRange(Request.Headers.Range, length, out long start, out long endInclusive))
        {
            content.Dispose();
            Response.Headers.ContentRange = $"bytes */{length}";

            return new StatusCodeResult<Status.RangeNotSatisfiable>();
        }

        // Ownership of the content passes to the result, which disposes it when the body has been
        // written - or abandoned. Nothing here holds it past this return.
        return new EncryptedBodyResult(content, transfer.Key, Convert.FromHexString(ivHex), start, endInclusive, _logger);
    }

    /// <summary>
    /// The success arm: headers, then the file encrypted from the requested offset, streamed in
    /// <see cref="BufferSize"/> pieces. A result rather than inline in the action so the streaming
    /// lives with the resource it owns, and so the action's return type can name it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An <see cref="IResult"/>, not an <see cref="ActionResult"/>, and public rather than
    /// private</b> - both forced by the rule in <c>AGENT-NOTES.md</c> §7 that an action's return type
    /// names every answer it can give. A <c>Results&lt;…&gt;</c> union is built from
    /// <see cref="IResult"/> arms, and an arm of a public action's signature cannot be a private
    /// type.
    /// </para>
    /// <para>
    /// It documents nothing of its own, which is why the action keeps
    /// <c>[ProducesResponseType]</c> for its 200 and 206: a status this class decides at run time is
    /// exactly the case <c>notes/typed-results.md</c> keeps the attribute for.
    /// </para>
    /// </remarks>
    public sealed class EncryptedBodyResult : IResult
    {
        private readonly ITransferContent _content;
        private readonly byte[] _key;
        private readonly byte[] _iv;
        private readonly long _start;
        private readonly long _endInclusive;
        private readonly ILogger _logger;

        public EncryptedBodyResult(ITransferContent content, byte[] key, byte[] iv, long start, long endInclusive, ILogger logger)
        {
            _content = content;
            _key = key;
            _iv = iv;
            _start = start;
            _endInclusive = endInclusive;
            _logger = logger;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            HttpResponse response = httpContext.Response;
            CancellationToken cancellationToken = httpContext.RequestAborted;

            using (_content)
            {
                long length = _content.Length;
                long count = _endInclusive - _start + 1;

                // 206 whenever the fetch does not begin at zero: firmware fails a resumed download that
                // is answered 200 (download.cpp:234). A full-file request that happens to carry
                // "bytes=0-" is a 200 - which is also what firmware's first request looks like.
                bool partial = _start != 0 || _endInclusive != length - 1;

                response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
                response.ContentLength = count;
                response.ContentType = "application/octet-stream";
                response.Headers[ContentEncryptionModeHeader] = AesCtr;

                if (partial)
                {
                    response.Headers.ContentRange = $"bytes {_start}-{_endInclusive}/{length}";
                }

                using TransferCipher cipher = new(_key, _iv, _start);

                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

                try
                {
                    long remaining = count;
                    long offset = _start;

                    while (remaining > 0)
                    {
                        int want = (int)Math.Min(BufferSize, remaining);
                        int read = await _content.ReadAsync(buffer.AsMemory(0, want), offset, cancellationToken);

                        if (read == 0)
                        {
                            // The file shrank under us. Content-Length has already gone out, so the
                            // printer sees a short body and fails the transfer - which is the honest
                            // outcome, and better than padding it with anything.
                            _logger.LogWarning("Transfer content ended at {Offset} with {Remaining} bytes still owed.",
                                               offset, remaining);

                            return;
                        }

                        cipher.Transform(buffer.AsSpan(0, read));

                        await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                        offset += read;
                        remaining -= read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }

    /// <summary>
    /// Parses firmware's <c>Range</c> - <c>bytes=start-</c> or <c>bytes=start-end</c> - into an
    /// inclusive span within <paramref name="length"/>. No header means the whole file.
    /// </summary>
    /// <returns>False for anything unsatisfiable, including a start the cipher cannot begin at.</returns>
    private static bool TryReadRange(string? header, long length, out long start, out long endInclusive)
    {
        start = 0;
        endInclusive = length - 1;

        if (string.IsNullOrEmpty(header))
        {
            return length > 0;
        }

        // One range only. Firmware sends exactly one, and a multipart response is not something it
        // could read.
        if (!RangeHeaderValue.TryParse(header, out RangeHeaderValue? range)
            || range.Unit != "bytes"
            || range.Ranges.Count != 1)
        {
            return false;
        }

        RangeItemHeaderValue item = range.Ranges.First();

        // A suffix range ("bytes=-500") is legal HTTP and not something firmware sends; refused
        // rather than served, because a suffix start is almost never block-aligned.
        if (item.From is not long from)
        {
            return false;
        }

        start = from;
        endInclusive = item.To is long to ? Math.Min(to, length - 1) : length - 1;

        return start >= 0
               && start < length
               && start <= endInclusive
               && start % TransferCipher.BlockSize == 0;
    }
}
