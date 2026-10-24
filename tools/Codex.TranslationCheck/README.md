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

## Using OmnisGM's Russian SRD

Before machine-translating a content type, check for an existing translation (see `tools/omnisgm-import`). The Russian spells are built from
[OmnisGM](https://github.com/OmnisGM-App/OmnisGM-Rules) (CC BY 4.0, © 2026 Petr Radilov) and fall back to the machine draft where the checker finds an error
or the translation's sentence count differs from its own English:

```bash
python3 tools/omnisgm-import/import_spells.py <omnisgm>/src/dnd/srd-5.2 content/systems/srd5e/packs/srd-5.2.1 /tmp/omnis-pack /tmp/divergent.txt
dotnet run --project tools/Codex.TranslationCheck -- --lang ru --translation /tmp/omnis-pack > /tmp/omnis-report.txt
python3 tools/omnisgm-import/merge_with_fallback.py /tmp/omnis-pack/spell/spells.yaml <machine-draft spells.yaml> /tmp/omnis-report.txt /tmp/divergent.txt <output spells.yaml>
```

`import_monsters.py` and `import_items.py` work the same way for creatures and magic items (no fallback: pass `-` to `merge_with_fallback.py` and entries that fail stay English).
