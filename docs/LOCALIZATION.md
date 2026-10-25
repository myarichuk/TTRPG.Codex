# Localization

TTRPG.Codex ships in English, Russian and Hebrew (right-to-left). Three layers are translated, each in the
way that suits it. The golden rule for all of them: **keys, ids and logic stay in the original language; only
display text is translated**, because the rules engine compares stored values (class names, dropdown values,
tags) against pack data.

## 1. The interface (web app and launcher)

- Text lives in `src/Codex.Localization/Resources/` as `.resx` files, one set per *area*: `Strings.Combat.resx`
  (English, neutral), `Strings.Combat.ru.resx`, `Strings.Combat.he.resx`. A key's first segment names its area
  (`Combat.Round` is in `Strings.Combat*.resx`); keys with no area file live in `Strings.resx`. Adding an
  area is just adding the three files; there is no registration. Do not name an area with two letters (it
  could collide with a culture code).
- In a Razor component the injected `L` (`Localizer`) looks text up: `@L["Combat.Round"]`, or with arguments
  `L["Combat.Hp", current, max]`. Keys must be string literals at the call site. A test
  (`LocalizationTests`) fails if a key used in source is missing, if the three languages disagree on keys or
  `{0}` placeholders, or if a value is empty.
- Counted text uses ICU-style plurals, with the right categories per language (ru: one/few/many/other,
  he: one/two/other): `{0, plural, one{# round} other{# rounds}}`; `#` is the number. Build a sentence as one
  message with placeholders; never glue fragments together, because word order differs.
- Errors a user should read are thrown as `LocalizedException(key, args)` and shown with `L.Describe(ex)`;
  the exception's `Message` stays English for logs.
- Never render resource text through `MarkupString`. Split sentences around markup instead.
- Right-to-left: `<html dir>` follows the language and the CSS uses logical properties (`margin-inline-start`,
  `inset-inline-end`, `text-align: start`). Use `<LucideIcon>` for arrows (back/next icons mirror
  automatically) and wrap dice and number tokens inside sentences in `<bdi>`.
- The language is chosen with the header/sign-in picker (`/set-culture`, cookie + remembered on the user),
  else the install default the launcher passes as `Codex:DefaultCulture`, else the browser's
  `Accept-Language`.
- The launcher keeps its language in `launcher.json` (`Language`, default: the OS language), relabels live and
  starts the server with `Codex__DefaultCulture`.
- AI features write in the reader's language (`AiPrompts.StyleFor`); image prompts stay English.
- Keep terms consistent with [i18n-glossary.md](i18n-glossary.md). Machine-drafted translations want a native
  review before a release.

## 2. A game system's labels

`system.yaml` can have siblings `system.ru.yaml`, `system.he.yaml` (any two-letter language tag) holding display
text keyed by the keys the system already uses (attribute/resource/field keys, entity `type`s, condition
names, dropdown values). See `SystemTranslation` for the shape and `content/systems/srd5e/system.ru.yaml`
for an example. `SystemDefinition.Localize(culture)` returns a display copy; the UI uses
`ISystemCatalog.GetLocalizedDefinition`. Rules code uses the original: the copy keeps each attribute's original
words (`CanonicalLabel`) so rules that match "Intelligence" or "INT" in pack data still work, and dropdown
values are shown with `FieldDefinition.Display(value)` but stored unchanged.

## 3. Content packs

A **translation pack** is a pack whose manifest has `locale` (`ru`) and `translationOf` (the pack it
translates, also listed in `dependencies`). It uses the same type folders as the pack it translates and each
file lists `id` plus the display text to translate: `name`, `description`, `flavor` (and `habitat`/`behavior`)
and free-text or table properties. It never replaces or hides an entry, so rules keep running on the original;
`IContentRegistry.Localize(entry, culture)` overlays the text at display time (falling back ru-RU → ru → the
original) and `Search(..., culture)` matches both the translated and the original name. Translation packs must
be sibling folders of the pack they translate, because the SRD importer regenerates (and deletes)
`packs/srd-5.2.1`. `content/systems/srd5e/packs/codex-flavor-ru` is an example.

What is translated today (all machine-drafted; the English text stays authoritative):

| | Russian | Hebrew |
|---|---|---|
| UI, launcher, system labels | yes | yes |
| Names and lore of classes, subclasses, species, backgrounds, feats, conditions (`codex-flavor-<lang>`) | yes | yes |
| Flavor of spells | yes | yes (names too, in `srd-5.2.1-he`) |
| Flavor of creatures and magic items | yes | yes |
| Rules text and names: classes, subclasses, species, backgrounds, feats, conditions, rule entries (`srd-5.2.1-<lang>`) | yes (OmnisGM, CC BY 4.0) | yes (own draft) |
| Rules text and names: spells | yes | yes |
| Rules text and names: creatures, magic items, equipment | yes | yes (own draft) |

Hebrew terminology follows the community Hebrew SRD (5esrd.co.il) as a vocabulary reference only: see
[i18n-he-terms.md](i18n-he-terms.md) for every term decision and [i18n-he-native-review.md](i18n-he-native-review.md) for the names a Hebrew speaker should confirm (languages, weapon masteries and properties, class and subclass feature names). The Hebrew drafts keep numbers, dice (`8d6`) and units (feet)
exactly as in the English so the checker can verify them; `tools/Codex.TranslationCheck/translator-prompt-he*.md` (spells, character options, creatures, items and equipment),
`prepare_flavor.py`, `merge_he_batches.py` and `harmonize_he_gear.py` (makes gear words in creatures and items match the equipment names) are the pipeline. Properties whose values are English display words used as data
(`school`, `classes`, `size`, tags) stay canonical and are shown through the system's value translations.
