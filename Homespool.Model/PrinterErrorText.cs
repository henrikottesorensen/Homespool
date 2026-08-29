using System.Collections.Generic;

namespace Homespool.Model;

/// <summary>
/// Maps the error code a printer reports on an <c>ATTENTION</c> or <c>ERROR</c> to the
/// sentence Prusa write for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generated - do not edit.</b> Run <c>tools/error-codes/generate.py</c> against a
/// Prusa-Firmware-Buddy checkout to refresh it. Source of truth is that repository's
/// <c>lib/Prusa-Error-Codes</c> submodule; this file was generated from
/// <c>c941c80f0</c> on 2026-08-29 and holds 53 codes.
/// </para>
/// <para>
/// <b>Only the codes Prusa mark <c>type: CONNECT</c></b> - the ones they intend to travel
/// this protocol. The key is the code with its per-model prefix stripped, because the same
/// fault arrives as 23829 from an MK3.5 and 31829 from a Core One.
/// </para>
/// <para>
/// <b>These sentences are the printer's words, not ours</b>, so they are not localised -
/// the same boundary <c>PrintJob.Reason</c> already sits on, where firmware's own refusal
/// text is passed through and the chrome around it is translated. An unknown code yields
/// no sentence rather than a fabricated one.
/// </para>
/// <para>
/// Text from Prusa Research's <c>Prusa-Error-Codes</c> (GPL-3.0).
/// </para>
/// </remarks>
public static class PrinterErrorText
{
    private static readonly Dictionary<int, string> Texts = new()
    {
        { 801, "Please complete Calibrations & Tests before using the printer." }, // UNFINISHED_SELFTEST
        { 802, "New firmware available" }, // PRINT_PREVIEW_NEW_FW
        { 803, "The G-code isn't fully compatible" }, // PRINT_PREVIEW_WRONG_PRINTER
        { 804, "Filament not detected. Load filament now?\\nSelect NO to cancel the print.\\nSelect DISABLE FS to disable the filament sensor and continue print." }, // PRINT_PREVIEW_NO_FILAMENT
        { 805, "A filament specified in the G-code is either not loaded or wrong type." }, // PRINT_PREVIEW_WRONG_FILAMENT
        { 806, "Filament detected. Unload filament now? Select NO to start the print with the currently loaded filament." }, // PRINT_PREVIEW_MMU_FILAMENT_INSERTED
        { 807, "File error" }, // PRINT_PREVIEW_FILE_ERROR
        { 808, "The heatbed cooled down during the power outage, printed object might have detached. Inspect it before continuing." }, // POWER_PANIC_COLD_BED
        { 809, "Length of an axis is too long.\\nMotor current is too low, probably.\\nRetry check, pause or resume the print?" }, // CRASH_RECOVERY_AXIS_LONG
        { 810, "Length of an axis is too short.\\nThere's an obstacle or bearing issue.\\nRetry check, pause or resume the print?" }, // CRASH_RECOVERY_AXIS_SHORT
        { 811, "Repeated collision has been detected.\\nDo you want to resume or pause the print?" }, // CRASH_RECOVERY_REPEATED_CRASH
        { 812, "Unable to home the printer.\\nDo you want to try again?" }, // CRASH_RECOVERY_HOME_FAIL
        { 813, "Toolchanger problem has been detected.\\nPark all tools to docks\\nand leave the carriage free." }, // CRASH_RECOVERY_TOOL_PICKUP
        { 814, "Changes of mapping available only in the Printer UI. Select Print to start the print with defaults." }, // PRINT_PREVIEW_TOOLS_MAPPING
        { 815, "Waiting for user input" }, // MMU_LOAD_UNLOAD_ERROR
        { 816, "Print fan not spinning. Check it for possible debris, then inspect the wiring." }, // PRINT_FAN_ERROR
        { 817, "Heating disabled due to 30 minutes of inactivity." }, // HEATERS_TIMEOUT
        { 818, "Measured temperature is not matching expected value. Check the thermistor is in contact with hotend. In case of damage, replace it." }, // HOTEND_TEMP_DISCREPANCY
        { 819, "Heating disabled due to 30 minutes of inactivity." }, // NOZZLE_TIMEOUT
        { 820, "Steppers disabled due to inactivity." }, // STEPPERS_TIMEOUT
        { 821, "USB drive or file error, the print is now paused. Reconnect the drive." }, // USB_FLASH_DISK_ERROR
        { 822, "Heatbreak thermistor is disconnected. Inspect the wiring." }, // HEATBREAK_THERMISTOR_FAIL
        { 823, "Nozzle doesn't seem to have round cross section. Make sure it is clean and perpendicular to the bed." }, // NOZZLE_DOES_NOT_HAVE_ROUND_SECTION
        { 824, "G-Code transfer running too slow. Check your network for issues or use different USB drive. Press Continue to resume printing." }, // NOT_DOWNLOADED
        { 825, "MCU in Buddy is overheated, likely due to exceeding the printer's operating temperature. Prevent overheating for optimal performance." }, // BUDDY_MCU_MAX_TEMP
        { 826, "MCU in Dwarf is overheated, likely due to exceeding the printer's operating temperature. Prevent overheating for optimal performance." }, // DWARF_MCU_MAX_TEMP
        { 827, "MCU in Modular Bed is overheated, likely due to exceeding the printer's operating temperature. Prevent overheating for optimal performance." }, // MOD_BED_MCU_MAX_TEMP
        { 828, "Hotend fan not spinning. Check it for possible debris, then inspect the wiring." }, // HOTEND_FAN_ERROR
        { 829, "Please replace filament." }, // FILAMENT_RUNOUT
        { 830, "Enclosure fan not spinning. Check it for possible debris, then inspect the wiring." }, // ENCLOSURE_FAN_ERROR
        { 831, "The HEPA filter is nearing the end of its life span. We recommend purchasing a new one." }, // ENCLOSURE_FILTER_EXPIRATION_WARNING
        { 832, "The HEPA filter has expired. Change the HEPA filter before your next print." }, // ENCLOSURE_FILTER_EXPIRATION
        { 833, "Bed leveling failed. Try again?" }, // PROBING_FAILED
        { 834, "Nozzle cleaning failed." }, // NOZZLE_CLEANING_FAILED
        { 835, "Quick Pause" }, // QUICK_PAUSE
        { 836, "Filament loading timed out." }, // FILAMENT_LOADING_TIMEOUT
        { 837, "Ensure the top ventilation grille is open for proper airflow." }, // OPEN_CHAMBER_VENTS
        { 838, "Ensure the top ventilation grille is closed for optimal chamber temperature." }, // CLOSE_CHAMBER_VENTS
        { 839, "Chamber cooling fan is not spinning. Check it for possible debris, then inspect the wiring." }, // CHAMBER_COOLING_FAN_ERROR
        { 840, "Chamber filtration fan is not spinning. Check it for possible debris, then inspect the wiring." }, // CHAMBER_FILTRATION_FAN_ERROR
        { 841, "Would you like to purge the filament?\\n\\nIt will then retract to prevent oozing. Be careful, the nozzle is hot!" }, // NOZZLE_CLEANING_FAILED_RECOMMEND_PURGE
        { 842, "Waiting for nozzle temperature..." }, // NOZZLE_CLEANING_FAILED_WAIT_TEMP
        { 843, "Purging the filament.\\n\\nPlease wait until the purge is complete." }, // NOZZLE_CLEANING_FAILED_PURGE
        { 844, "The filament has been purged.\\n\\nThe nozzle will now retract the filament to prevent oozing." }, // NOZZLE_CLEANING_FAILED_AUTORETRACT
        { 845, "Remove the purged filament and ensure the nozzle is clean and ready.\\n\\nBe careful, the nozzle is hot!" }, // NOZZLE_CLEANING_FAILED_REMOVE_FILAMENT
        { 846, "The auto-retract feature is disabled, which might have caused the failure.\\n\\nDo you want to enable auto-retract?" }, // NOZZLE_CLEANING_FAILED_AUTORETRACT_ENABLE_ASK
        { 847, "Are you sure you want to abort the print?\\n\\nThe current print will be cancelled and you will need to start over." }, // NOZZLE_CLEANING_FAILED_ABORT_ASK
        { 848, "G-Code signed by an identity not trusted by this printer. Do you want to save identity as trusted?\\n\\nIdentity name: %s\\nIdentity key hash: %s" }, // UNTRUSTED_IDENTITY
        { 849, "Dock fan is not spinning. Check for debris and inspect the wiring." }, // DOCK_FAN_ERROR
        { 850, "The nozzle cleaner is full. Empty it to prevent overflow. Caution: the nozzle and print bed may be hot." }, // NOZZLE_CLEANER_FULL
        { 851, "The nozzle cleaner may overflow during this print." }, // NOZZLE_CLEANER_MAY_OVERFILL
        { 852, "Empty the nozzle cleaner, then press Done." }, // NOZZLE_CLEANER_EMPTY
        { 854, "The filament is not compatible with the current printer hardware setup." }, // FILAMENT_INCOMPATIBLE
    };

    /// <summary>
    /// The sentence for a code as the wire spells it (five digits, model prefix included),
    /// or null when the catalogue does not describe it.
    /// </summary>
    public static string? For(int? code)
    {
        if (code is not { } value)
        {
            return null;
        }

        return Texts.TryGetValue(value % 1000, out string? text) ? text : null;
    }
}
