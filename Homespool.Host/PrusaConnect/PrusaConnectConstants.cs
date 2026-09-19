namespace Homespool.Host.PrusaConnect;

/// <summary>
/// How much of what a printer states is taken: how long the identity strings may be - the serial,
/// the fingerprint, the printer type and the firmware version - and, below those, how long the
/// strings telemetry and events carry may be and how many slots there can be.
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

    /// <summary>
    /// How long a printer's own token is - firmware's <c>Printer::Config::CONNECT_TOKEN_LEN</c>
    /// (<c>src/connect/printer.hpp:173</c> at the pinned ref).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An exact length here and a ceiling on the wire, which is why one number does three jobs.</b>
    /// <c>TokenService</c> generates exactly this many characters (15 bytes of CSPRNG, Base64) and
    /// refuses anything shorter as a presented token; <c>SetToken</c> may not exceed it, because a
    /// longer one is refused by the printer.
    /// </para>
    /// <para>
    /// <b>Firmware enforces it two different ways, and neither is a reason to keep two constants.</b>
    /// <c>SET_TOKEN</c> rejects outright - <c>command.cpp:316</c> tests <c>len - 1 &lt;= </c> the
    /// length and answers <c>BrokenCommand{"Token too long"}</c> - while the registration response
    /// header is copied character by character <i>while</i> the index is below it
    /// (<c>registrator.cpp:133</c>, into a buffer one byte longer for the terminator), so an
    /// over-long one there is silently truncated rather than refused. Twenty is accepted whole on
    /// both paths; the strict <c>&lt;</c> bounds a write index, not the acceptable length.
    /// </para>
    /// <para>
    /// <b>Do not merge this with <c>PrusaConnectOptions.PrinterHostMaxLength</c>.</b> That is also 20
    /// and is a different firmware buffer entirely - the Connect hostname. They agree by coincidence.
    /// </para>
    /// </remarks>
    public const int PrinterTokenLength = 20;

    /// <summary>Longest firmware string accepted, e.g. <c>6.4.0+11974</c>.</summary>
    public const int FirmwareMaxLength = 64;

    /// <summary>
    /// The highest slot or tool number the wire can describe. Telemetry's <c>slot</c> block and an
    /// <c>INFO</c>'s <c>tools</c> block key their entries 1..this, and nothing outside it is read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Firmware's ceiling rather than a guess.</b> <c>Printer::Params::slot_mask</c> is a
    /// <c>uint8_t</c> carrying a <c>static_assert(8 * sizeof slot_mask >= VirtualToolIndex::count)</c>
    /// (<c>printer.hpp:130</c>), and the keys are rendered <c>state.iter + 1</c>, so eight is the top
    /// of the representation with no headroom.
    /// </para>
    /// <para>
    /// <b>The bound is what keeps a slot a small thing.</b> Every slot number a printer states becomes
    /// a stored row that is never removed and is copied into every later sample, so an unbounded key
    /// is a row count chosen by whoever holds one printer's token.
    /// </para>
    /// </remarks>
    public const int MaxSlotNumber = 8;

    /// <summary>
    /// Longest filament name accepted - the flat <c>material</c>, a slot's and an <c>INFO</c> tool's.
    /// Firmware's name buffer holds seven characters (<c>filament_name_buffer_size</c>,
    /// <c>filament.hpp</c>).
    /// </summary>
    public const int MaterialMaxLength = 32;

    /// <summary>
    /// Longest <c>slot.command</c> accepted. Firmware renders one character (<c>"%c"</c>,
    /// <c>render.cpp</c>).
    /// </summary>
    public const int MmuCommandMaxLength = 8;

    /// <summary>
    /// Longest filament-sensor state accepted, for either sensor. Firmware sends one word of a closed
    /// set.
    /// </summary>
    public const int FilamentSensorStatusMaxLength = 32;

    /// <summary>
    /// Longest event <c>reason</c> accepted. Firmware's are string literals in <c>planner.cpp</c>, the
    /// longest of them 34 characters.
    /// </summary>
    public const int ReasonMaxLength = 256;

    /// <summary>
    /// Longest attention <c>text</c> or <c>title</c> accepted. Both come from firmware's error-code
    /// table, whose longest text runs to about 250 characters.
    /// </summary>
    public const int AttentionTextMaxLength = 512;

    /// <summary>
    /// Largest drive listing kept, as UTF-8 bytes of its <c>children</c> array. A real drive of 69
    /// files measured 12 KB, so this is room for well over a thousand.
    /// </summary>
    /// <remarks>
    /// <b>The listing is one row per printer, replaced on every report</b>, so this bounds what one
    /// report costs to write rather than anything that accumulates.
    /// </remarks>
    public const int DriveListingMaxBytes = 256 * 1024;
}
