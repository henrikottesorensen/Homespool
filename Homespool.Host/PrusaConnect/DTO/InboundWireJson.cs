using System.Text.Json;
using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO;

/// <summary>
/// How a printer's message is read into its DTOs. One set of options for every inbound shape,
/// because what it says is true of the protocol rather than of any one of them.
/// </summary>
internal static class InboundWireJson
{
    /// <summary>
    /// The defaults, plus the quoted literals <c>"NaN"</c>, <c>"Infinity"</c> and <c>"-Infinity"</c>
    /// read as the floats they name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A printer's bare <c>nan</c> is rewritten to the quoted form before parsing - see
    /// <see cref="NonFiniteNumberPatcher"/> - and a read that does not accept that form throws on
    /// it, which costs the printer its connection: the very thing the rewrite exists to prevent.
    /// </para>
    /// <para>
    /// <b>The DTO then carries a float that is not finite, on purpose.</b> What that should mean is
    /// decided where it would be stored (<see cref="Homespool.Host.Telemetry.FiniteFloat"/>), which has to deal
    /// with the infinity a valid <c>1e39</c> parses to in any case.
    /// </para>
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}
