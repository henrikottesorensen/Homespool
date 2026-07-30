using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Certificates;

/// <summary>
/// Resolves a name to addresses, behind an interface so the judgement that uses it can be tested
/// without a DNS server.
/// </summary>
/// <remarks>
/// The one caller asks a narrow question — <i>can a printer on the household LAN reach this name?</i>
/// — and an empty answer means "no idea", never "no". That distinction is the whole reason this is an
/// interface rather than a call to <c>Dns</c>: a test has to be able to produce "resolves to a
/// container address", "resolves to a LAN address" and "does not resolve" on demand, and only one of
/// those is safe to act on.
/// </remarks>
public interface IHostAddressResolver
{
    /// <summary>
    /// The addresses <paramref name="name"/> resolves to, or empty if it cannot be resolved.
    /// </summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken);
}
