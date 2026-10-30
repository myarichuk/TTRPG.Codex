#!/usr/bin/env node
// Static checks on site/dist, no browser needed (CI runs this): every local link, image, stylesheet and
// #anchor resolves; every image has alt text; ids are unique; each page declares its language and direction.
import { readFile, readdir, stat } from 'node:fs/promises';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const base = new URL(process.env.SITE_URL || 'https://myarichuk.github.io/TTRPG.Codex/').pathname; // site-absolute links start here
const dist = join(dirname(fileURLToPath(import.meta.url)), '..', 'dist');
const problems = [];
const bad = (file, msg) => problems.push(`${relative(dist, file).split(sep).join('/')}: ${msg}`);

const walk = async (dir) => (await Promise.all((await readdir(dir, { withFileTypes: true })).map((e) => (e.isDirectory() ? walk(join(dir, e.name)) : [join(dir, e.name)])))).flat();
const files = await walk(dist);
const pages = files.filter((f) => f.endsWith('.html'));
const exists = async (p) => { try { return await stat(p); } catch { return null; } };

const idCache = new Map();
const idsOf = async (file) => {
  if (!idCache.has(file)) idCache.set(file, new Set([...(await readFile(file, 'utf8')).matchAll(/\sid="([^"]+)"/g)].map((m) => m[1])));
  return idCache.get(file);
};

for (const file of pages) {
  const html = await readFile(file, 'utf8');
  if (!/<html lang="(en|ru|he)" dir="(ltr|rtl)"/.test(html)) bad(file, 'missing lang/dir on <html>');
  if (/lang="he"/.test(html.slice(0, 200)) && !/dir="rtl"/.test(html.slice(0, 200))) bad(file, 'Hebrew page is not rtl');
  const ids = [...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1]);
  for (const id of new Set(ids.filter((x, i) => ids.indexOf(x) !== i))) bad(file, `duplicate id "${id}"`);
  for (const m of html.matchAll(/<img\b[^>]*>/g)) if (!/\salt="/.test(m[0])) bad(file, `image without alt: ${m[0].slice(0, 80)}`);
  if (!/<title>[^<]{5,}<\/title>/.test(html)) bad(file, 'missing <title>');
  if (!/<meta name="description" content="[^"]{40,}"/.test(html)) bad(file, 'missing or short meta description');

  const refs = [...html.matchAll(/\s(?:href|src)="([^"]*)"/g)].map((m) => m[1]);
  for (const ref of refs) {
    if (!ref || /^(https?:|mailto:|data:|tel:|javascript:)/.test(ref)) continue;
    const [pathPart, hash] = ref.split('#');
    const target = pathPart ? (pathPart.startsWith(base) ? join(dist, pathPart.slice(base.length)) : resolve(dirname(file), pathPart)) : file;
    let real = target;
    const info = await exists(target);
    if (!info) { bad(file, `broken link "${ref}"`); continue; }
    if (info.isDirectory()) real = join(target, 'index.html');
    if (!(await exists(real))) { bad(file, `broken link "${ref}" (no index.html)`); continue; }
    if (hash && real.endsWith('.html') && !(await idsOf(real)).has(decodeURIComponent(hash))) bad(file, `missing anchor "${ref}"`);
  }
}
for (const required of ['index.html', 'ru/index.html', 'he/index.html', 'install/index.html', 'guide/index.html', 'ru/install/index.html', 'he/guide/index.html', 'sitemap.xml', 'robots.txt', '404.html', 'assets/og.png']) {
  if (!(await exists(join(dist, required)))) problems.push(`missing ${required}`);
}

if (problems.length) { console.error(`${problems.length} problem(s):\n${problems.map((p) => `  - ${p}`).join('\n')}`); process.exit(1); }
console.log(`OK: ${pages.length} pages checked`);
