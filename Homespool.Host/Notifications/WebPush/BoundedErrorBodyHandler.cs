using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Notifications.WebPush;

/// <summary>
/// Reads an error response's body itself, under a size cap and the request's cancellation token, before
/// the push library gets to it.
/// </summary>
/// <remarks>
/// The library reads a failed delivery's body with <c>ReadAsStringAsync()</c> - whole, with no
/// cancellation token, after <c>HttpClient.Timeout</c> has stopped applying. A push service that
/// answered a 400 with an endless body would hold a delivery open for good. Buffered here first, the
/// library's read is of memory; a body over the cap is dropped, since only its status is ever acted on.
/// </remarks>
public sealed class BoundedErrorBodyHandler : DelegatingHandler
{
    /// <summary>
    /// Enough for any push service's explanation. What is read is never shown or logged verbatim, so
    /// there is no reason to hold more.
    /// </summary>
    public const long MaxErrorBodyBytes = 8 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                 CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                await response.Content.LoadIntoBufferAsync(MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // Over the cap. The status is the whole of the answer; the body is replaced by nothing.
                response.Content.Dispose();
                response.Content = new ByteArrayContent([]);
            }
        }

        return response;
    }
}
