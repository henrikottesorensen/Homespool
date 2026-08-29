#!/usr/bin/env python3
"""Generate PrinterErrorText.cs from Prusa's own error-code catalogue.

    ./tools/error-codes/generate.py [--firmware /path/to/Prusa-Firmware-Buddy]

Reads lib/Prusa-Error-Codes/yaml/buddy-error-codes.yaml (a submodule of the firmware
tree) and emits the code -> sentence map that turns an ATTENTION's bare "23829" into
"Please replace filament."

WHY ONLY THE "CONNECT" ENTRIES
==============================
The catalogue holds 255 codes; 59 carry type: "CONNECT", which is Prusa marking them as
meant to travel over this protocol rather than being internal numbers that leaked. Those
are exactly the ones a printer can put on our wire, so the rest would be dead weight -
and shipping them would imply we can explain screens we never receive.

WHY THE LAST THREE DIGITS ARE THE KEY
=====================================
Catalogue codes are written "XX829", where XX is a per-model prefix the printer fills in
(23 = MK3.5, 31 = Core One). The same fault therefore arrives as a different five-digit
number from each model, so the printer prefix is stripped and only the fault survives as
the key.

SIX CODES HAVE PER-MODEL WORDING, AND THE BROADEST WINS
=======================================================
A handful (XX802, XX805, XX817, XX819, XX831, XX832) appear twice: once for MINI and once
for everything else, differing only in phrasing ("New FW available" against "New firmware
available"). Rather than carry a second dimension for six cosmetic variants, the entry
covering more models wins. A MINI would then read the majority sentence, which describes
the same fault in different words - the failure mode is a slightly-off phrase, not a wrong
explanation.

WHY THE OUTPUT IS COMMITTED
===========================
Same arrangement as tools/printer-models/generate.py: the firmware checkout is a
*developer-time* dependency for regenerating, never a build-time one. A clean build and CI
compile the committed .cs like any other source.

Staleness is benign and additive: an unknown code renders as no sentence at all, leaving
the state word alone - the honest-nulls rule, not a fabricated explanation.

LICENCE
=======
The catalogue is Prusa Research's Prusa-Error-Codes, GPL-3.0. Homespool is AGPL-3.0, which
GPL-3.0 material may be combined with. The generated file carries the attribution.
"""

import argparse
import re
import subprocess
import sys
from datetime import date
from pathlib import Path

DEFAULT_FIRMWARE = Path.home() / "Prusa" / "Prusa-Firmware-Buddy"
SOURCE = Path("lib/Prusa-Error-Codes/yaml/buddy-error-codes.yaml")
OUTPUT = Path(__file__).resolve().parents[2] / "Homespool.Model" / "PrinterErrorText.cs"

# One catalogue entry starts at "  - code:" and runs to the next one. Fields within vary
# (printers, gui_layout, approved and the comment blocks are all optional), so each is
# picked out by name rather than by position.
ENTRY_SPLIT = re.compile(r"\n  - code: ")


def field(block: str, name: str) -> str | None:
    """A quoted scalar field of one entry, or None when the entry omits it."""
    match = re.search(r'\n    %s: "(.*?)"\n' % name, block, re.DOTALL)
    return match.group(1) if match else None


def firmware_revision(firmware: Path) -> str:
    """The checkout's SHA, so the generated file records what it was read from."""
    try:
        out = subprocess.run(
            ["git", "-C", str(firmware), "rev-parse", "--short", "HEAD"],
            capture_output=True, text=True, check=True)
        return out.stdout.strip()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return "unknown"


def escape(text: str) -> str:
    """A yaml scalar as a C# string literal: quotes, backslashes and newlines."""
    return (text.replace("\\", "\\\\").replace('"', '\\"')
                .replace("\n", "\\n").replace("\r", ""))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--firmware", type=Path, default=DEFAULT_FIRMWARE)
    args = parser.parse_args()

    source = args.firmware / SOURCE
    if not source.exists():
        print(f"not found: {source}", file=sys.stderr)
        return 1

    blocks = ENTRY_SPLIT.split(source.read_text())[1:]

    # key -> (text, model count), so a later entry only displaces an earlier one when it
    # covers more models. See the module docstring on per-model wording.
    chosen: dict[int, tuple[str, int]] = {}
    ids: dict[int, str] = {}

    for block in blocks:
        if 'type: "CONNECT"' not in block:
            continue

        code = block.split("\n", 1)[0].strip().strip('"')
        if not code.startswith("XX") or not code[2:].isdigit():
            print(f"skipping unexpected code {code!r}", file=sys.stderr)
            continue

        # The sentence, not the title: titles are frequently just a severity word
        # ("Warning", "Recommendation") while the text is what explains the fault.
        # Where a title is the more specific of the two it duplicates the text anyway.
        text = field(block, "text") or field(block, "title")
        if not text:
            continue

        key = int(code[2:])
        printers = re.search(r"\n    printers: \[(.*?)\]", block)
        breadth = len(printers.group(1).split(",")) if printers else 99

        if key not in chosen or breadth > chosen[key][1]:
            chosen[key] = (text, breadth)
            ids[key] = field(block, "id") or ""

    revision = firmware_revision(args.firmware)
    lines = [
        "using System.Collections.Generic;",
        "",
        "namespace Homespool.Model;",
        "",
        "/// <summary>",
        "/// Maps the error code a printer reports on an <c>ATTENTION</c> or <c>ERROR</c> to the",
        "/// sentence Prusa write for it.",
        "/// </summary>",
        "/// <remarks>",
        "/// <para>",
        "/// <b>Generated - do not edit.</b> Run <c>tools/error-codes/generate.py</c> against a",
        "/// Prusa-Firmware-Buddy checkout to refresh it. Source of truth is that repository's",
        "/// <c>lib/Prusa-Error-Codes</c> submodule; this file was generated from",
        f"/// <c>{revision}</c> on {date.today().isoformat()} and holds {len(chosen)} codes.",
        "/// </para>",
        "/// <para>",
        "/// <b>Only the codes Prusa mark <c>type: CONNECT</c></b> - the ones they intend to travel",
        "/// this protocol. The key is the code with its per-model prefix stripped, because the same",
        "/// fault arrives as 23829 from an MK3.5 and 31829 from a Core One.",
        "/// </para>",
        "/// <para>",
        "/// <b>These sentences are the printer's words, not ours</b>, so they are not localised -",
        "/// the same boundary <c>PrintJob.Reason</c> already sits on, where firmware's own refusal",
        "/// text is passed through and the chrome around it is translated. An unknown code yields",
        "/// no sentence rather than a fabricated one.",
        "/// </para>",
        "/// <para>",
        "/// Text from Prusa Research's <c>Prusa-Error-Codes</c> (GPL-3.0).",
        "/// </para>",
        "/// </remarks>",
        "public static class PrinterErrorText",
        "{",
        "    private static readonly Dictionary<int, string> Texts = new()",
        "    {",
    ]

    for key in sorted(chosen):
        comment = f" // {ids[key]}" if ids[key] else ""
        lines.append(f'        {{ {key}, "{escape(chosen[key][0])}" }},{comment}')

    lines += [
        "    };",
        "",
        "    /// <summary>",
        "    /// The sentence for a code as the wire spells it (five digits, model prefix included),",
        "    /// or null when the catalogue does not describe it.",
        "    /// </summary>",
        "    public static string? For(int? code)",
        "    {",
        "        if (code is not { } value)",
        "        {",
        "            return null;",
        "        }",
        "",
        "        return Texts.TryGetValue(value % 1000, out string? text) ? text : null;",
        "    }",
        "}",
        "",
    ]

    OUTPUT.write_text("\n".join(lines))
    print(f"wrote {OUTPUT} ({len(chosen)} codes from {revision})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
