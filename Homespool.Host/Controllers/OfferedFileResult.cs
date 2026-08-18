using System;
using System.Buffers;
using System.Net.Mime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.Controllers;

/// <summary>
/// Writes an offered file to the response as it is, for a printer that fetches over HTTP and cannot
/// decrypt anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>The plaintext sibling of <c>EncryptedTransferController.EncryptedBodyResult</c>.</b> Same job -
/// stream an <see cref="ITransferContent"/> to a printer and dispose it whether the body completes or
/// is abandoned - without the cipher, because the client this exists for has no way to undo one.
/// </para>
/// <para>
/// <b>No range handling.</b> The Python SDK issues a plain <c>requests.get</c> with no <c>Range</c>
/// header and no resume: a failed download is retried from zero. Answering ranges we are never asked
/// for would be untested code on a transfer path, which is the kind that fails quietly and late.
/// </para>
/// </remarks>
public sealed class OfferedFileResult : IResult, IEndpointMetadataProvider
{
    /// <summary>Big enough that a large model is not a syscall per page, small enough to pool.</summary>
    private const int BufferSize = 64 * 1024;

    private readonly ITransferContent _content;

    public OfferedFileResult(ITransferContent content)
    {
        _content = content;
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            StatusCodes.Status200OK, typeof(void), [MediaTypeNames.Application.Octet]));
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            // Stated, because a printer wants to know how much is coming - the SDK reads it straight
            // into the transfer's progress, and without it there is nothing to show.
            httpContext.Response.ContentLength = _content.Length;
            httpContext.Response.ContentType = MediaTypeNames.Application.Octet;

            long offset = 0;
            CancellationToken cancellationToken = httpContext.RequestAborted;

            while (offset < _content.Length)
            {
                int read = await _content.ReadAsync(buffer.AsMemory(0, BufferSize), offset, cancellationToken)
                                         .ConfigureAwait(false);

                if (read <= 0)
                {
                    // The file shrank under us - a delete racing the transfer. Nothing useful can be
                    // said at this point: the headers have gone, so the printer sees a short body and
                    // reports the transfer failed, which is the truth.
                    break;
                }

                await httpContext.Response.Body
                                 .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                                 .ConfigureAwait(false);

                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            // Ours to close whether the body completed or the printer went away mid-transfer.
            _content.Dispose();
        }
    }
}
