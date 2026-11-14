# TTRPG.Codex website

The project site on GitHub Pages: a landing page, an install guide and a usage guide, in English, Russian and
Hebrew (right to left). It is a small static generator with no framework: `build.mjs` turns Markdown and JSON
into plain HTML, one stylesheet, one small script. Every page works without JavaScript.

```sh
cd site
npm ci               # marked (the Markdown parser); Playwright only for capturing screenshots
npm run build        # writes site/dist
node tools/check.mjs # links, anchors, alt text, language and direction (also runs in CI)
npm run serve        # build, then preview at http://localhost:4173/
node tools/audit.mjs # browser audit of the preview: overflow, images, theme, lightbox, menu
```

## Layout

| Path | What |
|---|---|
| `src/content/<lang>/site.json` | Navigation, footer and every section of the home page. All languages share one shape; the build fails if a key is missing. |
| `src/content/<lang>/install.md`, `guide.md`, `systems.md` | The long pages, in Markdown with a small front matter. `systems.md` is the "Make your own system" guide. |
| `src/assets/` | CSS, JS, the app's own fonts and icon, and the screenshots. |
| `tools/` | Screenshot capture, optimiser, preview server, checks. |
| `build.mjs` | The generator. |

## Writing content

- **App labels are never typed by hand.** `[[ui:Campaign.Settings.Export]]` in Markdown (or `{{ui:...}}` inside a
  `site.json` string) prints the button or menu name exactly as the app shows it, in that page's language, read
  from `src/Codex.Localization/Resources/*.resx`. An unknown or untranslated key fails the build, and the labels
  cannot drift when the app is renamed.
- `![Caption](shot:table)` places a screenshot (`phone-*` ones in a phone frame); the language's own capture is used.
- `> [!NOTE]`, `> [!TIP]` and `> [!WARNING]` make callouts.
- Every `##` and `###` heading ends in `{#id}`. The ids must be identical in every language, so switching language
  keeps the reader's place; the build checks this.
- Code blocks are coloured at build time by highlight.js (pinned in `package.json`; `yaml` and `bash`, with `sh`, `shell`
  and `zsh` as aliases). Tag a fence with its language; the page ships no script for it. The colours are in `site.css`
  (`--hl-*`) and are at least 4.5:1 on the code background in both themes. Keep code blocks identical in every language.
- Icons come from the app's own Lucide set (`LucideIcon.razor`).
- Use the terms in `docs/i18n-glossary.md`. Prefer plural or neutral Hebrew, as the app does.

## Adding a language

Add the code to `LANGS` in `build.mjs`, the names in `langNames`/`langShort`, a `src/content/<code>/` folder with
the four files above, and the language to the screenshot script. Add the culture to the app first
(`docs/LOCALIZATION.md`); the labels come from its resources.

## Screenshots

They are captured from the real app, in each language, so the site never shows a mock-up.

```sh
# 1. a throwaway app with the test-seed endpoint on (never enable this on a real install)
ASPNETCORE_URLS=http://127.0.0.1:5199 Codex__DataDirectory=/tmp/codex-shots \
Codex__EnableTestSeedEndpoint=true dotnet run --project src/Codex.Web

# 2. drive it (needs a fresh data directory every time, because the demo users are fixed)
node site/tools/capture-screenshots.mjs --base http://127.0.0.1:5199 --langs en,ru,he

# 3. WebP + size manifest + social preview image into src/assets
python3 site/tools/optimize-screenshots.py site/.capture
```

The demo campaign (names, texts, languages) is `tools/demo-data.mjs`. If a screen changes, rerun and commit the
WebP files.

Options for step 2: `--book-only true` sets the campaign up and takes only the book's screens (the print panel,
the pictures tray and the editor: its chapters, Ask AI, the party leak check and a stale chapter); `--chrome <path>`
uses an installed Chrome when Playwright's own browser is not downloaded; `--python <path>` picks the Python that
has Pillow. The printed-pages picture (`book-pages`) also needs poppler's `pdftoppm`, and is skipped without it.
Nothing in the script asks a model or draws a picture, and it never types a key: the AI and picture screens are
taken before any run.

## Deployment

`.github/workflows/pages.yml` builds, checks and deploys on every push to `main` that touches the site or the
app's UI strings. In the repository settings, set **Pages > Source** to **GitHub Actions** once. Pull requests build
and check the site without deploying it.
