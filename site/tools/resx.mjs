// Reads the app's own UI strings (src/Codex.Localization/Resources/*.resx) so the website quotes
// buttons and menus exactly as the app shows them, in each language, and cannot drift from them.
import { readdir, readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
export const RESOURCES = join(here, '..', '..', 'src', 'Codex.Localization', 'Resources');

const unescapeXml = (s) =>
  s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');

const parse = (xml) => {
  const out = new Map();
  for (const m of xml.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)) out.set(m[1], unescapeXml(m[2]));
  return out;
};

/** All strings of one language, falling back to the neutral (English) text key by key. */
export async function loadStrings(lang) {
  const files = (await readdir(RESOURCES)).filter((f) => f.endsWith('.resx'));
  // Strings.resx, Strings.<Area>.resx are neutral; Strings[.<Area>].<lang>.resx are translations.
  const isCulture = (f) => /\.(ru|he)\.resx$/.test(f);
  const neutral = new Map();
  const localized = new Map();
  for (const f of files) {
    const target = isCulture(f) ? (f.endsWith(`.${lang}.resx`) ? localized : null) : neutral;
    if (!target) continue;
    for (const [k, v] of parse(await readFile(join(RESOURCES, f), 'utf8'))) target.set(k, v);
  }
  const all = new Map(neutral);
  if (lang !== 'en') for (const [k, v] of localized) all.set(k, v);
  // Which keys really are translated (the rest fell back to English), so callers can refuse a silent fallback.
  all.translated = lang === 'en' ? new Set(all.keys()) : new Set(localized.keys());
  return all;
}
