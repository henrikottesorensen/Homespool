#!/usr/bin/env python3
"""Generate PrinterModelNames.cs from Buddy firmware's own model table.

    ./tools/printer-models/generate.py [--firmware /path/to/Prusa-Firmware-Buddy]

Reads include/common/printer_model_data.hpp and emits the version-triple -> id_str
map that turns INFO's printer_type ("1.3.5") into a human name ("MK3.5").

WHY A TOOL RATHER THAN A HAND-COPIED TABLE, AND WHY THE OUTPUT IS COMMITTED
==========================================================================
The same arrangement as render-fixtures.json:
the firmware checkout is a *developer-time* dependency for regenerating, never a build-time
one. A clean build and CI need nothing from Prusa's 9 GB tree - they compile the committed
.cs file like any other source.

A build-time dependency would be badly out of proportion here. What it would buy is fifteen
display strings; what it would cost is a repository that cannot build without an external
checkout at a pinned SHA. Compare the things this project genuinely does pin to firmware -
the wire format, the ciphersuite, FINGERPRINT_HDR_SIZE - where being wrong breaks
connections rather than showing a code instead of a name.

Staleness here is benign and additive: a version triple cannot change meaning without
breaking every deployed printer, so an out-of-date table is missing new models rather than
wrong about old ones. Unknown models fall back to no name at all, which is the "honest
nulls rather than faked" rule PrinterReadDTO already follows.
"""

import argparse
import re
import subprocess
import sys
from datetime import date
from pathlib import Path

DEFAULT_FIRMWARE = Path.home() / "Prusa" / "Prusa-Firmware-Buddy"
SOURCE = Path("include/common/printer_model_data.hpp")
OUTPUT = Path(__file__).resolve().parents[2] / "Homespool.Model" / "PrinterModelNames.cs"

# One PrinterModelInfo block: the version triple and the id_str, in that order. Fields
# between them vary per entry (help_url, usb_pid, gcode_check_code), so the pattern spans
# them rather than assuming a fixed shape.
#
# EXPECT FEWER MATCHES THAN THERE ARE id_str LINES. The header holds two arrays:
# printer_model_info (versioned) and printer_model_mmu_variant (keyed by model, no version).
# As of 2026-07-28 that is 17 versioned against 28 id_str lines. Only the versioned ones are
# reachable from what a printer sends, since printer_type IS the version triple - so matching
# 17 of 28 is correct and complete rather than a partial parse.
#
# The MMU array is where Connect's printerModel ("MK4SISMMU3") comes from, as opposed to
# printerTypeName ("MK4"). It needs MMU state as well as the model, which is why the spec
# carries hasMmuEnabled separately and why we do not attempt printerModel at all.
ENTRY = re.compile(
    r"\.version\s*=\s*\{\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\}.*?\.id_str\s*=\s*\"([^\"]+)\"",
    re.DOTALL,
)


def firmware_revision(firmware: Path) -> str:
    """The checkout's SHA, so the generated file records what it was read from."""
    try:
        out = subprocess.run(
            ["git", "-C", str(firmware), "rev-parse", "--short", "HEAD"],
            capture_output=True, text=True, check=True)
        return out.stdout.strip()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return "unknown"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--firmware", type=Path, default=DEFAULT_FIRMWARE)
    args = parser.parse_args()

    source = args.firmware / SOURCE

    if not source.exists():
        print(f"not found: {source}\nPass --firmware if the checkout is elsewhere.", file=sys.stderr)
        return 1

    entries = ENTRY.findall(source.read_text())

    if not entries:
        # A silent empty table would compile and quietly answer null for every printer.
        print(f"parsed no entries from {source} - has the table's shape changed?", file=sys.stderr)
        return 1

    versioned = source.read_text().count(".version")

    if len(entries) != versioned:
        # Guards the subtler failure: a shape change that still parses, just not all of it.
        print(f"parsed {len(entries)} entries but found {versioned} .version fields - "
              "the table's shape has changed and this pattern no longer covers it.", file=sys.stderr)
        return 1

    rows = "\n".join(
        f'        ["{t}.{v}.{s}"] = "{name}",' for t, v, s, name in entries)

    OUTPUT.write_text(f'''using System.Collections.Generic;

namespace Homespool.Model;

/// <summary>
/// Maps the version triple a printer reports as <c>printer_type</c> ("1.3.5") to the name Prusa
/// call it ("MK3.5").
/// </summary>
/// <remarks>
/// <para>
/// <b>Generated - do not edit.</b> Run <c>tools/printer-models/generate.py</c> against a
/// Prusa-Firmware-Buddy checkout to refresh it. Source of truth is that repository's
/// <c>include/common/printer_model_data.hpp</c>; this file was generated from
/// <c>{firmware_revision(args.firmware)}</c> on {date.today()} and holds {len(entries)} models.
/// </para>
/// <para>
/// The firmware checkout is a <b>developer-time</b> dependency, never a build-time one - the
/// generated file is committed and compiles like any other source, so a clean build needs nothing
/// from Prusa's tree. Same arrangement as <c>render-fixtures.json</c>.
/// </para>
/// <para>
/// Staleness is additive and benign: a version triple cannot change meaning without breaking every
/// deployed printer, so an out-of-date table lacks new models rather than misnaming old ones. An
/// unknown triple yields no name at all, which is the honest-nulls rule the API already follows.
/// </para>
/// </remarks>
public static class PrinterModelNames
{{
    private static readonly Dictionary<string, string> Names = new()
    {{
{rows}
    }};

    /// <summary>
    /// The model name for a <c>printer_type</c> triple, or <c>null</c> if this table has never
    /// heard of it - a printer newer than the firmware checkout this was generated from.
    /// </summary>
    public static string? ForPrinterType(string? printerType)
    {{
        return printerType is not null && Names.TryGetValue(printerType, out string? name) ? name : null;
    }}
}}
''')

    print(f"{OUTPUT.name}: {len(entries)} models from {source}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
