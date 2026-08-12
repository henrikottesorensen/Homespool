# Translating `setup-env.sh`

There are no translations yet, and the script is ready for one. Every string a person reads is
marked `$"..."`, which bash looks up in the catalogue for the current locale and falls back to the
English written in the source when there is no translation, no catalogue, or no locale. So a clone
with this directory empty behaves exactly as it always did.

## Adding a language

```bash
bash --dump-po-strings setup-env.sh > po/setup-env.pot        # refresh the template
msginit -i po/setup-env.pot -l da_DK -o po/da.po              # start a translation
$EDITOR po/da.po
msgfmt -o po/locale/da/LC_MESSAGES/homespool-setup.mo po/da.po
```

Commit both the `.po` and the compiled `.mo`. The script is run straight from a clone and there is
no build step to compile catalogues at install time.

When the English changes, `msgmerge -U po/da.po po/setup-env.pot` carries the existing translations
across and marks what moved.

## Two things worth knowing before starting

**The messages are paragraphs, not lines.** They are wrapped where they are printed, to the width of
the reader's terminal, so a translation may be any length and does not need to match the original's
line breaks. Do not add your own.

**Windows via `setup-env.cmd` will not be translated.** That path runs the wizard inside the
Homespool container, which has only the `C`, `C.utf8` and `POSIX` locales - and glibc ignores
`LANGUAGE` under `C`. Generating a locale in the image costs about 19 MB and was judged not worth it
(2026-08-11): WSL2 is the documented route for a Windows machine that cannot install Docker Desktop,
and there the host's own locale applies normally.

macOS is fine, including Apple's `/bin/bash` 3.2, which does translate `$"..."`.
