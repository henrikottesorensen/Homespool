using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Homespool.Model.Entities;

/// <summary>
/// The last-known state of a printer: exactly one row per printer, upserted on every
/// telemetry message. This table never grows with time — it backs the live view.
/// </summary>
/// <remarks>
/// <para>
/// Prusa telemetry is partial. Observed in the <c>websucket</c> capture, a printer alternates
/// between two message shapes: a "full" one carrying all 17 fields, and a "slim" one carrying
/// only <c>state</c>, <c>job_id</c>, <c>progress</c>, <c>time_printing</c> and
/// <c>time_remaining</c>. Roughly 45% of messages are slim.
/// </para>
/// <para>
/// So every field here is nullable, and merging must <b>only overwrite fields present in the
/// incoming message</b> — a slim message must not null out the temperatures. A null therefore
/// means "never reported by this printer", not "reported as absent".
/// </para>
/// </remarks>
public class PrinterLiveState
{
    /// <summary>Primary key. Also the FK to <see cref="Printer"/> — this is a 1:1 extension.</summary>
    /// <remarks>
    /// Deliberately no <c>Printer</c> navigation, only the key. These instances live in
    /// <c>TelemetryWriter</c>'s cache for the whole process lifetime, crossing one <c>DbContext</c>
    /// per flush - and a navigation is a slot EF's relationship fix-up writes each context's
    /// tracked <c>Printer</c> into, poisoning the cached instance for every context after it. The
    /// writer used to defend against exactly that by nulling the navigation before each attach;
    /// with no navigation, there is nothing to defend.
    /// </remarks>
    [Key]
    public int PrinterId { get; set; }

    /// <summary>When the most recent telemetry message was received.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    // -- Core, present on both message shapes ------------------------------------------------
    public PrinterStatus Status { get; set; }

    /// <summary>
    /// Why the printer is waiting, as the error code it reported - or null when it is not waiting,
    /// or is waiting for something it did not put a code on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lifted from <c>STATE_CHANGED</c>, because telemetry cannot carry it.</b> A status sample
    /// says <c>ATTENTION</c> and no more; the code rides the event's <c>data</c> object. Without it
    /// the page can only say that a printer wants somebody, which is the state it was in when this
    /// column was added: a red banner with no reason under it.
    /// </para>
    /// <para>
    /// <b>Cleared when the printer stops waiting</b>, so a stale code cannot outlive the dialog it
    /// describes and explain the wrong thing later. Kept as the number it is - the wire's five-digit
    /// spelling has a per-model prefix, which <see cref="PrinterErrorText"/> strips.
    /// </para>
    /// </remarks>
    public int? AttentionCode { get; set; }

    /// <summary>
    /// The printer's own sentence about why it is waiting, when it volunteers one - null for an
    /// ordinary attention, where only <see cref="AttentionCode"/> is sent.
    /// </summary>
    /// <remarks>
    /// Firmware fills this only on a red screen (its <c>ErrorPrinter</c>), so the usual reading is
    /// "decode the code". Stored anyway because when the printer does say something, its own words
    /// beat our catalogue: the catalogue can be out of date about a machine, and the machine cannot.
    /// </remarks>
    public string? AttentionText { get; set; }

    public int? JobId { get; set; }

    /// <summary>Percent complete, 0-100.</summary>
    public int? Progress { get; set; }

    /// <summary>Seconds elapsed on the current job.</summary>
    public int? TimePrinting { get; set; }

    /// <summary>Seconds remaining on the current job, as estimated by the printer.</summary>
    public int? TimeRemaining { get; set; }

    // -- Present only on full messages -------------------------------------------------------
    public float? NozzleTemperature { get; set; }

    public float? BedTemperature { get; set; }

    public float? TargetNozzleTemperature { get; set; }

    public float? TargetBedTemperature { get; set; }

    public int? Speed { get; set; }

    public int? Flow { get; set; }

    public string? Material { get; set; }

    public float? XAxis { get; set; }

    public float? YAxis { get; set; }

    public float? ZAxis { get; set; }

    public int? ExtruderFan { get; set; }

    public int? PrintFan { get; set; }

    /// <summary>Filament this printer has ever extruded, in millimetres.</summary>
    /// <remarks>
    /// <para>
    /// <b>A lifetime odometer, not a per-print figure</b> - 1 013 555.875 mm on the MK3.5 when it was
    /// first read, and it does not reset when a print starts. Subtracting two readings is the only way
    /// to get what one print used.
    /// </para>
    /// <para>
    /// <b>Which is why it is the one telemetry field that survives a print ending.</b> Firmware guards
    /// the job block with <c>if (params.has_job)</c> and this sits outside it, so
    /// <c>PrinterLiveStateMerger</c> deliberately carries it forward where it clears
    /// <see cref="JobId"/>, <see cref="Progress"/> and the rest: a figure that stops rising when
    /// nothing extrudes is correct, where a progress that stops falling is a lie.
    /// </para>
    /// <para>
    /// <b>But it is not monotonic, because the printer's EEPROM can be reset</b> (Henrik, 2026-08-10) -
    /// so it goes backwards exactly once, unpredictably, and a machine that has extruded a kilometre
    /// then reports zero with nothing anywhere flagging it as odd. <b>Anything subtracting two readings
    /// must treat a decrease as the counter resetting rather than as negative extrusion</b>, which is a
    /// branch to write deliberately and not an assumption to make.
    /// </para>
    /// <para>
    /// It is also the only honest signal that a print has actually begun - <c>PRINTING</c> arrives with
    /// a cold nozzle, and plastic followed 168 s later on a measured run.
    /// <b>That use survives a reset</b> where a subtraction does not: it
    /// waits for the value to <i>increase</i>, so a reset merely means it counts up from zero instead
    /// of from a kilometre. The signal is a change, not a level.
    /// </para>
    /// </remarks>
    public float? FilamentUsed { get; set; }

    /// <summary>Seconds until the next filament change.</summary>
    public int? TimeToFilamentChange { get; set; }

    // -- Chamber (flattened; fixed shape) ----------------------------------------------------
    public float? ChamberTemperature { get; set; }

    public int? ChamberTargetTemperature { get; set; }

    public int? ChamberFan1Rpm { get; set; }

    public int? ChamberFan2Rpm { get; set; }

    public int? ChamberFanPwmTarget { get; set; }

    public int? ChamberLedIntensity { get; set; }

    // -- Enclosure (flattened; fixed shape) --------------------------------------------------
    //
    // XL only. Note the telemetry "enclosure" object is NOT the same shape as the one in the INFO
    // event, which instead carries enabled / printing_filtration / post_print / filter_lifetime /
    // filtration_filaments. All three fields below are integers in the firmware.
    public int? EnclosureTemperature { get; set; }

    public int? EnclosureFanRpm { get; set; }

    public int? EnclosureTimeInUse { get; set; }

    // -- Prusa iX (flattened; fixed shape) ---------------------------------------------------
    public float? HeatbreakTemperature { get; set; }

    public float? PsuTemperature { get; set; }

    public float? AmbientTemperature { get; set; }

    public string? ExtruderFilamentSensorStatus { get; set; }

    public string? RemoteFilamentSensorStatus { get; set; }

    // -- Slots (multi-tool printers only) -----------------------------------------------------
    //
    // The firmware emits the "slot" object only when enabled_tool_cnt() > 1, so all three of these
    // stay null on a single-tool printer.

    /// <summary>Which slot is currently active. Wire field <c>slot.active</c>, 1-based.</summary>
    public int? ActiveSlot { get; set; }

    /// <summary>
    /// MMU progress code. Wire field <c>slot.state</c>, emitted only on MMU builds.
    /// </summary>
    public int? MmuState { get; set; }

    /// <summary>
    /// MMU command code — a single character. Wire field <c>slot.command</c>, MMU builds only.
    /// </summary>
    public string? MmuCommand { get; set; }

    /// <summary>Last-known values for each slot. Empty on single-tool printers.</summary>
    public virtual ICollection<PrinterLiveSlotState> Slots { get; set; } = [];
}
