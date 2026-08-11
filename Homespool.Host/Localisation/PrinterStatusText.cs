using Microsoft.Extensions.Localization;

using Homespool.Model;

namespace Homespool.Host.Localisation;

/// <summary>
/// A printer's status in a person's words, in their language.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first user of the enum display seam, and the reason it exists.</b> The switch this
/// replaces ended in <c>status.Value.ToString()</c> — so eleven of the thirteen states reached the
/// page as the enum's own identifier, which is a C# name that happened to read like English. That
/// works exactly until it has to be a different language, and then there is nothing to translate
/// because nobody ever wrote the words down.
/// </para>
/// <para>
/// <b>Only the words are localised. The badge colour is not</b>, and the pair is worth looking at
/// together: <c>StatusBadgeClass</c> returns <c>text-bg-danger</c>, which is a CSS class name — a
/// value Bootstrap parses, not a value a person reads. Translating it would produce a class that
/// styles nothing. Same enum, same page, two sides of the boundary
/// <c>notes/localisation.md</c> §2 draws.
/// </para>
/// <para>
/// <b>A missing key falls back rather than throws.</b> If a state is added to
/// <see cref="PrinterStatus"/> and nobody adds a resource for it, the enum name still reaches the
/// page — the behaviour there has always been, and a new firmware state showing as <c>Cooldown</c>
/// is a great deal better than a page that will not render.
/// </para>
/// <para>
/// <b><see cref="PrinterStatus.Manipulating"/> deliberately has no resource</b>, and is what that
/// fallback is demonstrated on. Buddy cannot produce it: firmware's <c>DeviceState</c>
/// (<c>printer_state.hpp</c>) has ten members and no such value, and <c>ParseWireState</c> is the
/// only writer of a live status. It reached <see cref="PrinterStatus"/> because Connect's own
/// twelve-value enum was transcribed wholesale (Henrik, December 2025 - see
/// <c>notes/protocol-vocabulary-boundary.md</c>), so writing words for it would be inventing a
/// sentence for a badge that cannot render, in two languages, with nothing able to check either.
/// <see cref="PrinterStatus.Offline"/> keeps its words on the opposite reasoning: Homespool could
/// synthesise that one itself, since it is a verdict about a connection rather than a report from a
/// printer.
/// </para>
/// </remarks>
public sealed class PrinterStatusText
{
    /// <summary>
    /// The shared part of every status key, so <see cref="PrinterStatus.Printing"/> reads
    /// <c>PrinterStatus_Printing</c>.
    /// </summary>
    private const string KeyPrefix = "PrinterStatus_";

    private readonly IStringLocalizer<SharedResource> _localiser;

    public PrinterStatusText(IStringLocalizer<SharedResource> localiser)
    {
        _localiser = localiser;
    }

    /// <summary>
    /// What a connected printer's status says.
    /// </summary>
    /// <remarks>
    /// <b>Null means connected but not yet heard from</b> - the socket is up and no telemetry has
    /// landed, which is a real few seconds after a printer connects, not an error.
    /// <see cref="PrinterStatus.Undefined"/> is the same thing from the other side: a stored value
    /// nobody wrote. Both say "Connected", which is the one true thing known about the printer.
    /// </remarks>
    public string For(PrinterStatus? status)
    {
        string name = status switch
        {
            null or PrinterStatus.Undefined or PrinterStatus.Unknown => "Connected",
            _ => status.Value.ToString(),
        };

        LocalizedString localised = _localiser[KeyPrefix + name];

        // ResourceNotFound means no resource for a state somebody added to the enum without adding
        // a word for it. The enum's own name is the same thing this method used to return.
        return localised.ResourceNotFound ? name : localised.Value;
    }
}
