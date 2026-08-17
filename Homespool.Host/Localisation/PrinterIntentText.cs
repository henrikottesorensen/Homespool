using Microsoft.Extensions.Localization;

using Homespool.Host.Printing;

namespace Homespool.Host.Localisation;

/// <summary>
/// What to call a printer intent when telling somebody it was sent or refused.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="IPrinterIntent.Name"/> is not a name for a person</b>, and says so: it is the type
/// name, <i>"for logs and failure bodies"</i>. Put into a status message it produces
/// <c>PausePrint sent.</c>, and in Danish <c>PausePrint er afsendt.</c> — an identifier from the
/// codebase, shown to whoever pressed a button.
/// </para>
/// <para>
/// <b>The defect predates the vocabulary refactor that surfaced it.</b> The page used to show
/// <c>WireName</c> instead, so it said <c>PAUSE_PRINT</c> — machine vocabulary either way. Renaming
/// changed which machine word appeared, not whether one did.
/// </para>
/// <para>
/// The same shape as <see cref="PrinterStatusText"/>, and for the same reason: an enum-ish domain
/// value reaching a sentence needs a seam, or the sentence carries the codebase's spelling into
/// somebody's language.
/// </para>
/// </remarks>
public sealed class PrinterIntentText
{
    /// <summary>Shared by every intent key, so <c>PausePrint</c> reads <c>Intent_PausePrint</c>.</summary>
    private const string KeyPrefix = "Intent_";

    private readonly IStringLocalizer<SharedResource> _localiser;

    public PrinterIntentText(IStringLocalizer<SharedResource> localiser)
    {
        _localiser = localiser;
    }

    /// <summary>
    /// The words for an intent, as a sentence about it would use them.
    /// </summary>
    /// <remarks>
    /// <b>Falls back to the type name rather than to the key</b>, because that is what the page
    /// showed before this existed — so an intent added without words is no worse than it was, rather
    /// than rendering <c>Intent_TrimFilament</c> at somebody. <c>EveryIntentHasWords</c> is what
    /// makes sure that fallback stays theoretical.
    /// </remarks>
    /// <param name="intent">The intent that was sent, or refused.</param>
    public string For(IPrinterIntent intent)
    {
        System.ArgumentNullException.ThrowIfNull(intent);

        LocalizedString localised = _localiser[KeyPrefix + intent.Name];

        return localised.ResourceNotFound ? intent.Name : localised.Value;
    }
}
