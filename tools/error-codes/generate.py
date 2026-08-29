#!/usr/bin/env python3
"""Generate PrinterErrorText.cs from Prusa's own error-code catalogue, in every language.

    ./tools/error-codes/generate.py [--firmware /path/to/Prusa-Firmware-Buddy]

Reads three things from a firmware checkout and one from beside this script:

  lib/Prusa-Error-Codes/yaml/buddy-error-codes.yaml   the codes and their English sentences
  src/lang/po/<lang>/*.po                             firmware's own translations of those sentences
  tools/error-codes/da.tsv                            Danish, which Prusa do not ship

and emits the code -> sentence map that turns an ATTENTION's bare "23829" into "Please replace
filament." - or "Bitte Filament ersetzen." for a reader who wants German.

WHY ONLY THE "CONNECT" ENTRIES
==============================
The catalogue holds 255 codes; 59 carry type: "CONNECT", which is Prusa marking them as meant to
travel over this protocol rather than being internal numbers that leaked. Those are exactly the
ones a printer can put on our wire, so the rest would be dead weight - and shipping them would
imply we can explain screens we never receive.

WHY THE LAST THREE DIGITS ARE THE KEY
=====================================
Catalogue codes are written "XX829", where XX is a per-model prefix the printer fills in (23 =
MK3.5, 31 = Core One). The same fault therefore arrives as a different five-digit number from each
model, so the printer prefix is stripped and only the fault survives as the key.

SIX CODES HAVE PER-MODEL WORDING, AND THE BROADEST WINS
=======================================================
A handful (XX802, XX805, XX817, XX819, XX831, XX832) appear twice: once for MINI and once for
everything else, differing only in phrasing ("New FW available" against "New firmware available").
Rather than carry a second dimension for six cosmetic variants, the entry covering more models
wins. A MINI would then read the majority sentence, which describes the same fault in different
words - the failure mode is a slightly-off phrase, not a wrong explanation.

WHY THE TRANSLATIONS COME FROM FIRMWARE RATHER THAN FROM US
===========================================================
Firmware wraps these sentences in N_(), so they are already translated - the same catalogue, the
same wording, checked by whoever checks Prusa's releases. Lifting the .po entries costs a lookup by
English string and gives eight languages that agree with what the machine's own screen says. The
alternative, translating 52 sentences ourselves, would produce a second set of words for the same
faults and no way to keep them in step.

Danish is the exception and has to be ours: Prusa ship no Danish, so a Danish reader's printer
shows English. tools/error-codes/da.tsv is that translation, hand-maintained beside this script so
regenerating cannot overwrite it.

FIRMWARE'S LINE BREAKS ARE DROPPED
==================================
Several sentences are laid out for a 4-inch LCD, with breaks mid-thought. This renders on a web
card, so the breaks become spaces - the words are unchanged.

WHY THE OUTPUT IS COMMITTED
===========================
Same arrangement as tools/printer-models/generate.py: the firmware checkout is a *developer-time*
dependency for regenerating, never a build-time one. A clean build and CI compile the committed
.cs like any other source.

Staleness is benign and additive: an unknown code renders as no sentence at all, leaving the state
word alone - the honest-nulls rule, not a fabricated explanation. An untranslated one falls back to
English, which is what firmware does too.

LICENCE
=======
The catalogue and its translations are Prusa Research's (Prusa-Error-Codes and
Prusa-Firmware-Buddy, both GPL-3.0). Homespool is AGPL-3.0, which GPL-3.0 material may be combined
with. The generated file carries the attribution.
"""

import argparse
import re
import subprocess
import sys
from datetime import date
from pathlib import Path

DEFAULT_FIRMWARE = Path.home() / "Prusa" / "Prusa-Firmware-Buddy"
CATALOGUE = Path("lib/Prusa-Error-Codes/yaml/buddy-error-codes.yaml")
TRANSLATIONS = Path("src/lang/po")
DANISH = Path(__file__).resolve().parent / "da.tsv"
OUTPUT = Path(__file__).resolve().parents[2] / "Homespool.Model" / "PrinterErrorText.cs"

ENTRY_SPLIT = re.compile(r"\n  - code: ")


def field(block: str, name: str) -> str | None:
    """A quoted scalar field of one entry, or None when the entry omits it."""
    match = re.search(r'\n    %s: "(.*?)"\n' % name, block, re.DOTALL)
    return match.group(1) if match else None


def one_line(text: str) -> str:
    """Firmware's LCD line breaks, as one line of prose."""
    return re.sub(r"\s+", " ", text.replace("\\n", " ")).strip()


def firmware_revision(firmware: Path) -> str:
    """The checkout's SHA, so the generated file records what it was read from."""
    try:
        out = subprocess.run(
            ["git", "-C", str(firmware), "rev-parse", "--short", "HEAD"],
            capture_output=True, text=True, check=True)
        return out.stdout.strip()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return "unknown"


def read_po(path: Path) -> dict[str, str]:
    """msgid -> msgstr, including the multi-line continuation form."""
    entries: dict[str, str] = {}
    msgid = msgstr = None
    mode = None

    def flush() -> None:
        if msgid and msgstr:
            entries[msgid] = msgstr

    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()

        if line.startswith('msgid "'):
            flush()
            msgid, msgstr, mode = line[7:-1], "", "id"
        elif line.startswith('msgstr "'):
            msgstr, mode = line[8:-1], "str"
        elif line.startswith('"') and mode:
            if mode == "id":
                msgid += line[1:-1]
            else:
                msgstr += line[1:-1]
        elif not line:
            flush()
            msgid = msgstr = None
            mode = None

    flush()
    return entries


def escape(text: str) -> str:
    """A sentence as a C# string literal."""
    return text.replace("\\", "\\\\").replace('"', '\\"')


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--firmware", type=Path, default=DEFAULT_FIRMWARE)
    args = parser.parse_args()

    catalogue = args.firmware / CATALOGUE
    if not catalogue.exists():
        print(f"not found: {catalogue}", file=sys.stderr)
        return 1

    # key -> (english, model count, id), so a later entry only displaces an earlier one when it
    # covers more models. See the module docstring on per-model wording.
    chosen: dict[int, tuple[str, int, str]] = {}

    for block in ENTRY_SPLIT.split(catalogue.read_text())[1:]:
        if 'type: "CONNECT"' not in block:
            continue

        code = block.split("\n", 1)[0].strip().strip('"')
        if not code.startswith("XX") or not code[2:].isdigit():
            print(f"skipping unexpected code {code!r}", file=sys.stderr)
            continue

        # The sentence, not the title: titles are frequently just a severity word ("Warning",
        # "Recommendation") while the text is what explains the fault.
        text = field(block, "text") or field(block, "title")
        if not text:
            continue

        printers = re.search(r"\n    printers: \[(.*?)\]", block)
        breadth = len(printers.group(1).split(",")) if printers else 99
        key = int(code[2:])

        if key not in chosen or breadth > chosen[key][1]:
            chosen[key] = (text, breadth, field(block, "id") or "")

    # language -> key -> sentence. English is the catalogue itself.
    languages: dict[str, dict[int, str]] = {"en": {k: one_line(v[0]) for k, v in chosen.items()}}

    po_root = args.firmware / TRANSLATIONS
    for directory in sorted(p for p in po_root.iterdir() if p.is_dir()):
        files = list(directory.glob("*.po"))
        if not files:
            continue

        po = read_po(files[0])
        translated = {}

        for key, (english, _, _) in chosen.items():
            # Looked up by the catalogue's own English, which is the msgid firmware extracts.
            hit = po.get(english)
            if hit:
                translated[key] = one_line(hit)

        if translated:
            languages[directory.name] = translated

    if DANISH.exists():
        danish = {}
        for line in DANISH.read_text(encoding="utf-8").splitlines():
            if not line.strip() or line.startswith("#"):
                continue
            code, _, text = line.partition("\t")
            if text:
                danish[int(code)] = one_line(text)
        if danish:
            languages["da"] = danish

    revision = firmware_revision(args.firmware)
    coverage = ", ".join(f"{lang} {len(rows)}" for lang, rows in sorted(languages.items()))

    lines = [
        "using System.Collections.Generic;",
        "",
        "namespace Homespool.Model;",
        "",
        "/// <summary>",
        "/// Maps the error code a printer reports on an <c>ATTENTION</c> or <c>ERROR</c> to the",
        "/// sentence Prusa write for it, in the reader's language where there is one.",
        "/// </summary>",
        "/// <remarks>",
        "/// <para>",
        "/// <b>Generated - do not edit.</b> Run <c>tools/error-codes/generate.py</c> against a",
        "/// Prusa-Firmware-Buddy checkout to refresh it. Sources are that repository's",
        "/// <c>lib/Prusa-Error-Codes</c> submodule and its own <c>src/lang/po</c> catalogues;",
        f"/// generated from <c>{revision}</c> on {date.today().isoformat()}.",
        f"/// Codes per language: {coverage}.",
        "/// </para>",
        "/// <para>",
        "/// <b>Only the codes Prusa mark <c>type: CONNECT</c></b> - the ones they intend to travel",
        "/// this protocol. The key is the code with its per-model prefix stripped, because the same",
        "/// fault arrives as 23829 from an MK3.5 and 31829 from a Core One.",
        "/// </para>",
        "/// <para>",
        "/// <b>The translations are firmware's own</b>, lifted from the <c>.po</c> files by matching",
        "/// the English sentence - so a reader sees the same words their printer's screen would show",
        "/// in the same language. <b>Danish is the exception</b>: Prusa ship none, so it is ours,",
        "/// maintained in <c>tools/error-codes/da.tsv</c>. An untranslated code falls back to",
        "/// English, and an unknown code to no sentence at all rather than a fabricated one.",
        "/// </para>",
        "/// <para>",
        "/// Text from Prusa Research's <c>Prusa-Error-Codes</c> and <c>Prusa-Firmware-Buddy</c>",
        "/// (GPL-3.0).",
        "/// </para>",
        "/// </remarks>",
        "public static class PrinterErrorText",
        "{",
        "    private static readonly Dictionary<string, Dictionary<int, string>> Texts = new()",
        "    {",
    ]

    for lang in sorted(languages):
        lines.append(f'        ["{lang}"] = new()')
        lines.append("        {")
        for key in sorted(languages[lang]):
            ident = chosen[key][2] if lang == "en" and key in chosen else ""
            comment = f" // {ident}" if ident else ""
            lines.append(f'            {{ {key}, "{escape(languages[lang][key])}" }},{comment}')
        lines.append("        },")

    lines += [
        "    };",
        "",
        "    /// <summary>",
        "    /// The sentence for a code as the wire spells it (five digits, model prefix included),",
        "    /// in <paramref name=\"language\"/> where that language has one and in English otherwise;",
        "    /// null when the catalogue does not describe the code at all.",
        "    /// </summary>",
        "    /// <param name=\"code\">The reported code, or null.</param>",
        "    /// <param name=\"language\">",
        "    /// A two-letter language code. Null, unknown, or a language Prusa have not translated all",
        "    /// falls back to English - which is also what the printer's own screen falls back to.",
        "    /// </param>",
        "    public static string? For(int? code, string? language = null)",
        "    {",
        "        if (code is not { } value)",
        "        {",
        "            return null;",
        "        }",
        "",
        "        int key = value % 1000;",
        "",
        "        if (language is not null",
        "            && Texts.TryGetValue(language, out Dictionary<int, string>? translated)",
        "            && translated.TryGetValue(key, out string? sentence))",
        "        {",
        "            return sentence;",
        "        }",
        "",
        "        return Texts[\"en\"].TryGetValue(key, out string? english) ? english : null;",
        "    }",
        "}",
        "",
    ]

    OUTPUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"wrote {OUTPUT} from {revision}: {coverage}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
