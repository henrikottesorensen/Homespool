namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Reading the material a printer reports, and telling "PLA" from "I don't know".
/// </summary>
/// <remarks>
/// <para>
/// <b><c>"---"</c> is firmware's sentinel for no filament set, and it is a value rather than an
/// absence.</b> <c>marlin_printer.cpp:238</c> fills the slot from
/// <c>config_store().get_filament_type(vt).parameters().name</c>, and <c>FilamentType::none</c>'s
/// name is the literal <c>"---"</c> (<c>filament.cpp:42-47</c>). Telemetry's own guard is
/// <c>if (!material.empty())</c> (<c>render.cpp:196</c>), which that string passes - so the field
/// <em>is</em> sent, carrying a sentinel, and a null check alone reads it as a material called
/// <c>---</c>.
/// </para>
/// <para>
/// <b>The distinction is load-bearing rather than cosmetic.</b> It is exactly the condition under
/// which firmware will run <c>M702</c> headless: <c>evaluate_preheat_conditions</c> reads the same
/// <c>config_store()</c> entry this string was rendered from, and opens a blocking dialog at the
/// panel when it is <c>FilamentType::none</c>. So "does the printer name its filament" and "can this
/// be driven from off-machine" are the same question, answered by the same byte on the wire.
/// </para>
/// <para>
/// <c>PrusaTelemetryMapping</c> already strips this on the <c>INFO</c> path, where it maps into
/// <see cref="Homespool.Model.Entities.PrinterTool.Material"/>. It does <b>not</b> on the telemetry
/// path, so <see cref="Homespool.Model.Entities.PrinterLiveState.Material"/> holds whatever the
/// printer said - which is why anything reading that column comes through here.
/// </para>
/// </remarks>
public static class LoadedFilament
{
    /// <summary>Firmware's "no filament type set" string, sent in the ordinary <c>material</c> field.</summary>
    public const string NoneSentinel = "---";

    /// <summary>
    /// The filament name a printer is reporting, or null where it is reporting that it has none.
    /// </summary>
    /// <param name="reported">
    /// The raw <c>material</c> value, e.g. <see cref="Homespool.Model.Entities.PrinterLiveState.Material"/>.
    /// </param>
    public static string? Of(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported))
        {
            return null;
        }

        string trimmed = reported.Trim();

        return trimmed == NoneSentinel ? null : trimmed;
    }

    /// <summary>Whether the printer has told us what is loaded.</summary>
    public static bool IsKnown(string? reported)
    {
        return Of(reported) is not null;
    }
}
