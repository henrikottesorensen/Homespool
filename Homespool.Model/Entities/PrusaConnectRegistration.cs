using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Homespool.Model.Entities;

/// <summary>
/// A pending code-exchange enrolment: the transient state between a printer POSTing to
/// <c>/p/register</c> and it redeeming its code for a token. One row per POST, consumed and deleted
/// the moment the token is issued, at which point the printer's standing credential lives in
/// <see cref="PrusaConnectAuthenticationData"/> instead.
/// </summary>
/// <remarks>
/// <para>
/// A table of its own rather than a "pending" state of <see cref="PrusaConnectAuthenticationData"/>,
/// so that the enrolled table means exactly one thing (an enrolled printer's credential, every field
/// required) and the USB-key provisioning channel can converge into it without forcing the
/// code/serial columns nullable.
/// </para>
/// <para>
/// <see cref="FingerPrint"/> is known immediately — it travels in the register POST body — unlike the
/// provisioning channel, where it is not learned until first contact. <b>It does not identify the
/// row</b>: the POST is anonymous, so a fingerprint can have several registrations pending at once -
/// the printer's, an attempt it abandoned, somebody else's - each with a code of its own, and only
/// <see cref="TemporaryCode"/> says which one a poll or a claim means.
/// </para>
/// <para>
/// <see cref="Model"/> and <see cref="Firmware"/> are kept for the claim page's list of printers
/// waiting to be added, and for nothing else: an enrolled printer states both again on every
/// <c>INFO</c>, which is where <see cref="Printer.Model"/> and <see cref="Printer.Firmware"/> come
/// from. A pending row has no <c>INFO</c> yet, so without these the list could only show a serial
/// number - a sticker on the back of the machine.
/// </para>
/// </remarks>
public class PrusaConnectRegistration
{
    public long Id { get; set; }

    /// <summary>
    /// The claimed printer, or null while the registration is still unclaimed. A user's claim sets
    /// this; the printer's own poll then redeems the code and the row is consumed.
    /// </summary>
    public int? PrinterId { get; set; }

    [ForeignKey(nameof(PrinterId))]
    public virtual Printer? Printer { get; set; }

    public required string SerialNumber { get; set; }

    public required string FingerPrint { get; set; }

    public required string TemporaryCode { get; set; }

    /// <summary>
    /// The <c>printer_type</c> triple the register POST stated, e.g. <c>1.3.5</c>. Anonymous input:
    /// whoever sent the POST chose it, so it is shown only through a table of known models.
    /// </summary>
    public required string Model { get; set; }

    /// <summary>
    /// The firmware version the register POST stated, e.g. <c>6.4.0+11974</c>. Anonymous input, as
    /// <see cref="Model"/> is, so it is shown only as the version it parses to.
    /// </summary>
    public required string Firmware { get; set; }

    public DateTimeOffset TemporaryCodeExpiry { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
