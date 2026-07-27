namespace Homespool.FakePrinter;

/// <summary>
/// One range request the printer sends while pulling a file from the server
/// (<c>transfers::Download::InlineRequest</c>, Prusa-Firmware-Buddy <c>download.hpp:190-198</c> at
/// the pinned ref, rendered at <c>render.cpp:100-119</c>).
/// </summary>
/// <param name="FileId">
/// The nonce this transfer generated for itself (<c>rand_u()</c>, download.cpp:481-492). The server
/// echoes it in every chunk header, and a chunk carrying any other value kills the transfer.
/// </param>
/// <param name="Start">First byte wanted, from the start of the file.</param>
/// <param name="End"><b>Inclusive</b> last byte - the length is <c>End - Start + 1</c>.</param>
/// <param name="Details">
/// Present only on the first request of a negotiation (download.cpp:545-552). A
/// <see cref="FakeTransfer"/> that renegotiates - a <c>RangeJump</c> - sends them again with a fresh
/// <see cref="FileId"/>.
/// </param>
public sealed record InlineRequest(uint FileId, long Start, long End, InlineRequestDetails? Details);

/// <summary>
/// The block a first request carries so the server can tell which offer it answers
/// (<c>InlineRequestDetails</c>, download.hpp:184-188).
/// </summary>
/// <param name="Hash">The token from the server's <c>START_CONNECT_DOWNLOAD</c>, quoted back.</param>
/// <param name="TeamId">Echoed from the same command.</param>
/// <param name="TransferId">This transfer's own id, which its terminal events also carry.</param>
public sealed record InlineRequestDetails(string Hash, ulong TeamId, int TransferId);
