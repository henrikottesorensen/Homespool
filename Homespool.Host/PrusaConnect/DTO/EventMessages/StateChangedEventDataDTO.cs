using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

/// <summary>
/// The <c>data</c> object on a <c>STATE_CHANGED</c> event - <b>why</b> a printer is waiting for
/// somebody, when it is waiting for somebody.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reason travels here and nowhere else.</b> Telemetry repeats <c>dialog_id</c> for the whole
/// duration of an attention but carries no code, so the state word ("Waiting for you") is the whole
/// of what a status sample can say. Firmware renders the code into this object instead
/// (<c>render.cpp</c>, the event's <c>data</c> block), and re-sends the event throughout the
/// attention rather than once.
/// </para>
/// <para>
/// <b><see cref="Title"/> and <see cref="Text"/> are usually absent, and that is not a defect.</b>
/// Only the error-screen client fills them (firmware's <c>ErrorPrinter</c>, for a red screen);
/// an ordinary attention leaves them null and offers the code alone. So the code is what has to be
/// decodable, and any words the printer does volunteer are preferred to our decoding of them.
/// </para>
/// <para>
/// <b>The code arrives as a five-digit string and is a number.</b> Firmware formats it
/// <c>"%05" PRIu16</c>, its leading two digits being a per-model prefix - the same fault is 23829
/// from an MK3.5 and 31829 from a Core One - which is why
/// <see cref="Homespool.Model.PrinterErrorText"/> keys on the remainder.
/// </para>
/// <para>
/// <c>buttons</c> is deliberately not modelled. It says what the dialog can be answered with, and
/// nothing here offers to answer one; a field with no reader would only imply a capability this
/// does not have.
/// </para>
/// </remarks>
public class StateChangedEventDataDTO
{
    /// <summary>The error code, as the five-digit string firmware spells it.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>The dialog's heading, when the printer supplies one. Usually null.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>The dialog's own sentence, when the printer supplies one. Usually null.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
