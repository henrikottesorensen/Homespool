using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Homespool.Model.Entities;

/// <summary>
/// A pending code-exchange enrolment: the transient state between a printer POSTing to
/// <c>/p/register</c> and it redeeming its code for a token. One row per in-flight registration;
/// consumed and deleted the moment the token is issued, at which point the printer's standing
/// credential lives in <see cref="PrusaConnectAuthenticationData"/> instead.
/// </summary>
/// <remarks>
/// <para>
/// This used to be the same table as <see cref="PrusaConnectAuthenticationData"/> — a single row
/// mutated in place from "pending" to "enrolled". Splitting the two lets the enrolled table mean
/// exactly one thing (an enrolled printer's credential, every field required) so that the USB-key
/// provisioning channel can converge into it without forcing the code/serial columns nullable.
/// </para>
/// <para>
/// <see cref="FingerPrint"/> is known immediately — it travels in the register POST body — unlike the
/// provisioning channel, where it is not learned until first contact.
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

    public DateTimeOffset TemporaryCodeExpiry { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
