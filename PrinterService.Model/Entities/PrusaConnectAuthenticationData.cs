using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrinterService.Model.Entities;

/// <summary>
/// An enrolled printer's standing credential: the fingerprint it presents on the wire and the hash of
/// the token it authenticates with. One row per enrolled printer, read on the hot path of every
/// authenticated request and the WebSocket upgrade (AGENT-NOTES §9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Enrolled state only.</b> This used to double as the pending code-exchange registration — a row
/// mutated in place from "has a code, no token" to "claimed, token issued" — which is why
/// <c>PrinterId</c> and <c>HashedToken</c> were once nullable and the auth handler carried a
/// "should be unreachable" null check. The two pending channels now live in
/// <see cref="PrusaConnectRegistration"/> (code exchange) and <see cref="PrusaConnectProvisioning"/>
/// (USB key), each of which converges here on completion. Every field is required: a row existing
/// <em>is</em> an enrolled printer.
/// </para>
/// <para>
/// Because both enrollment channels materialise a row here, a provisioned printer authenticates
/// through the identical single fingerprint lookup as a code-exchange one — there is no per-channel
/// branch on the request path.
/// </para>
/// </remarks>
public class PrusaConnectAuthenticationData
{
    public long Id { get; set; }

    /// <summary>The enrolled printer. Never null — a credential without a printer is not enrollment.</summary>
    public int PrinterId { get; set; }

    [ForeignKey(nameof(PrinterId))]
    public virtual Printer? Printer { get; set; }

    /// <summary>
    /// The value this printer is identified by: the 16-character form its firmware puts on every
    /// header, including the WebSocket upgrade. See <c>PrinterFingerprint</c> for why this, and not
    /// the longer form, is the key.
    /// </summary>
    public required string FingerPrintKey { get; set; }

    /// <summary>
    /// The full 50-character fingerprint, when it has been seen. Only <c>/p/register</c>'s body
    /// carries it (and, once telemetry is persisted, the <c>INFO</c> event), so a printer enrolled by
    /// USB key has null here until something tells us the rest. Recorded for diagnostics and future
    /// cross-checking; never used to identify a printer.
    /// </summary>
    public string? FullFingerPrint { get; set; }

    public required string HashedToken { get; set; }

    /// <summary>When enrollment completed — the token was issued (code exchange) or bound (USB key).</summary>
    public DateTimeOffset EnrolledAt { get; set; }
}
