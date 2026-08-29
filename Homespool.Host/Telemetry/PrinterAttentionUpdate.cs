namespace Homespool.Host.Telemetry;

/// <summary>
/// Why a printer is waiting for somebody, lifted out of a <c>STATE_CHANGED</c> event so it can be
/// stored beside the state rather than only inside the event's payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate carrier for the same reason <see cref="PrinterDriveListingUpdate"/> is one:</b> the
/// event row keeps what arrived, and anything that must also be readable as *current* is lifted to
/// a place that holds one value per printer. A reason found by scanning the event log would have to
/// guess whether it still applies.
/// </para>
/// <para>
/// Both members can be null while the update itself exists - a dialog the printer neither coded nor
/// described. That is different from a null update, which says the printer is not in a dialog at
/// all.
/// </para>
/// </remarks>
/// <param name="Code">
/// The error code, with its per-model prefix intact as the wire sent it - stripping belongs to
/// <see cref="Homespool.Model.PrinterErrorText"/>, so what is stored stays what was reported.
/// </param>
/// <param name="Text">
/// The printer's own sentence, on the rare event that carries one (a red screen). Preferred over
/// decoding <paramref name="Code"/>, because a catalogue can be out of date about a machine and the
/// machine cannot.
/// </param>
public sealed record PrinterAttentionUpdate(int? Code, string? Text);
