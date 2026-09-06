namespace Homespool.Host.PrusaConnect;

/// <summary>
/// How long the identity strings a printer states about itself may be - the serial, the fingerprint,
/// the printer type and the firmware version.
/// </summary>
/// <remarks>
/// <para>
/// <b>A printer states the same fields on two unrelated messages</b>: <c>POST /p/register</c> when it
/// first asks for a credential, and every <c>INFO</c> event afterwards. "How long may this be" has one
/// answer for both, and it lives here rather than on either message, because a bound owned by one
/// message is a bound the other quietly does not have.
/// </para>
/// <para>
/// <b>The failure that shape produces is silent.</b> Bound one message and not the other and a value
/// a printer cannot register with can be stated a second later instead, landing on a SQLite
/// <c>TEXT</c> column that is rewritten on every subsequent <c>INFO</c>. Two constants that merely
/// happen to agree report nothing when they stop agreeing.
/// </para>
/// <para>
/// <b>The two enforcement points are deliberately different, and only the numbers are shared.</b>
/// <see cref="DTO.RegisterPrinterRequestDTO"/> spends them as <c>[StringLength]</c> attributes, which
/// <c>[ApiController]</c> turns into a 400 before the action runs - refusing the whole message, which
/// costs a printer one of only three registration retries.
/// <c>PrusaTelemetryMapping.ToIdentity</c> spends them by dropping the offending field and keeping the
/// rest, because an <c>INFO</c> carries several facts and one bad string should not cost the others.
/// Each is right for its own message.
/// </para>
/// <para>
/// <b>They must stay <c>const</c>.</b> An attribute argument has to be a compile-time constant, so a
/// <c>static readonly</c> here would compile everywhere except the one place that enforces half of
/// them.
/// </para>
/// <para>
/// <b>Generous against what firmware actually sends</b>, because refusing a real printer is expensive:
/// firmware reads any non-2xx from registration as <c>OnlineError::Server</c>. The fingerprint is 50
/// characters in that body, a serial around twenty, and the type and version strings shorter still -
/// so each is a comfortable multiple of the real value rather than a fit to it.
/// </para>
/// <para>
/// <b>Not a defence against markup, and must not be recorded as one.</b> A stored string is safe to
/// render because the views encode it; the payload that made that necessary was 25 characters, inside
/// every number here. These bound an unbounded column being rewritten at a printer's discretion,
/// which is a storage question.
/// </para>
/// </remarks>
public static class PrusaConnectConstants
{
    /// <summary>Longest serial number accepted. Real ones are around twenty characters.</summary>
    public const int SerialNumberMaxLength = 64;

    /// <summary>
    /// Longest fingerprint accepted. Firmware sends 50 characters in the registration body, where the
    /// headers carry only the first 16 of the same buffer.
    /// </summary>
    /// <remarks>
    /// <b>Registration is the only place this is spent</b> - an <c>INFO</c> carries a fingerprint too,
    /// but nothing is taken from it, because the printer authentication handler reads its own column.
    /// It belongs here all the same: it is one of the identity strings, and splitting it out would put
    /// three of the four in one place and leave a reader wondering what was different about the fourth.
    /// </remarks>
    public const int FingerPrintMaxLength = 64;

    /// <summary>Longest printer type accepted, e.g. <c>1.3.5</c>.</summary>
    public const int PrinterTypeMaxLength = 32;

    /// <summary>Longest firmware string accepted, e.g. <c>6.4.0+11974</c>.</summary>
    public const int FirmwareMaxLength = 64;
}
