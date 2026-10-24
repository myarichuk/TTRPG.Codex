# Codex.TranslationCheck

Checks a machine-drafted translation pack against the English entries it translates, and prepares batches for translators.

```bash
# 1. split a type into batches of exact English text (blank lines visible)
dotnet run --project tools/Codex.TranslationCheck -- --lang ru --translation x --prepare spell --batch-size 12 --out /tmp/batches

# 2. each translator (see translator-prompt.md) writes batch-NN/out/spell/spells.yaml and runs the check below on it
# 3. merge the batches into content/systems/srd5e/packs/srd-5.2.1-<lang>/spell/spells.yaml and check the whole pack
dotnet run --project tools/Codex.TranslationCheck -- --lang ru --translation content/systems/srd5e/packs/srd-5.2.1-ru [--quiet]
```

Errors (exit code 1): changed or missing numbers, changed dice, lost headings/table rows/bullets, wrong or mixed script, fields the rules read, unknown or duplicate ids, empty name/description.
Warnings (for a person): extra numbers, paragraph/bold changes, length, untranslated Latin text, agreed terms (from `system.<lang>.yaml`) not found by stem.

The checks cannot judge wording. A native speaker still has to review the text.
