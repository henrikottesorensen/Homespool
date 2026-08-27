using System.Collections.Generic;

using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Replaces the token a printer authenticates with, remotely. <c>SET_TOKEN</c>.
/// </summary>
/// <remarks>
/// <para>
/// Firmware calls <c>printer.init_connect(params.token)</c> and answers <c>Finished</c>
/// (<c>planner.cpp:956</c>). It is in the <b>error-state whitelist</b> (<c>planner.cpp:249</c>,
/// alongside <c>SEND_INFO</c> and <c>RESET_PRINTER</c>), so it works on a printer sitting in an error
/// screen - Prusa plainly intend it as remote credential management rather than a convenience.
/// </para>
/// <para>
/// <b>The write is atomic</b> - <c>init_connect</c> is a journalled EEPROM transaction - so a printer
/// holds either the old token or the new one, never a torn value. That is what makes rotation safe
/// against power loss mid-command.
/// </para>
/// <para>
/// <b>Sending this is not rotation on its own.</b> The flow is: mint a provisioning row, send this, and let the
/// auth handler's existing rebind path adopt the new credential on the printer's next authenticated
/// request. The ordering hazard is the whole point - <b>never retire the old credential until the new
/// one has actually been used</b>, or a lost command locks the printer out with no way back short of
/// a USB stick.
/// </para>
/// </remarks>
public class SetToken : ISendableCommand
{
    /// <summary>
    /// Firmware's own ceiling, <c>Printer::Config::CONNECT_TOKEN_LEN</c> (<c>printer.hpp:173</c>).
    /// Longer and the printer answers <c>BrokenCommand{"Token too long"}</c> rather than failing
    /// quietly; <c>TokenService.PrinterTokenLength</c> is 20 for exactly this reason.
    /// </summary>
    public const int MaxTokenLength = 20;

    /// <summary>
    /// The replacement token, as text. <b>A string on the wire, not bytes</b> — firmware parses it
    /// with <c>is_arg("token", Type::String)</c> (<c>command.cpp:313</c>) and <c>strlcpy</c>s it into
    /// a fixed buffer. This property was <c>byte[]</c> while the class was an unsent marker, which
    /// would have serialised as base64 and been rejected on arrival.
    /// </summary>
    public required string Token { get; set; }

    public string WireName => "SET_TOKEN";

    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["token"] = Token,
    };

    /// <inheritdoc />
    /// <remarks>Re-tokening a printer is reprovisioning it, which is the manager's business.</remarks>
    public Capability RequiredCapability => Capability.ManagePrinter;
}
