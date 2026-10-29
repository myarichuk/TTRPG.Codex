#!/usr/bin/env node
// Builds the website into site/dist: a plain static site for GitHub Pages.
//
//   src/content/<lang>/site.json   chrome and the home page (every language has the same shape)
//   src/content/<lang>/<page>.md   long-form pages (install, guide) with a small front matter
//   src/assets/                    CSS, JS, fonts, screenshots (copied as they are)
//
// Markdown extras: `[[ui:Some.Key]]` prints a button or menu name exactly as the app shows it in that
// language (read from the app's .resx files; an unknown key fails the build), `![caption](shot:name)`
// places a screenshot in that language, `> [!NOTE]` / `[!TIP]` / `[!WARNING]` make callouts, and a
// heading ending in `{#id}` gets a stable id that is the same in every language.
import { cp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Marked } from 'marked';
import { loadStrings } from './tools/resx.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const SRC = join(here, 'src');
const DIST = join(here, 'dist');
const REPO = 'https://github.com/myarichuk/TTRPG.Codex';
const SITE_URL = (process.env.SITE_URL || 'https://myarichuk.github.io/TTRPG.Codex/').replace(/\/?$/, '/');
const LANGS = ['en', 'ru', 'he'];
const DOCS = ['install', 'guide'];
const problems = [];
const fail = (msg) => problems.push(msg);

// ---- inputs -------------------------------------------------------------------------------------
const read = (...p) => readFile(join(SRC, ...p), 'utf8');
const readJson = async (...p) => JSON.parse(await read(...p));

const icons = new Map();
{
  const razor = await readFile(join(here, '..', 'src', 'Codex.Web', 'Components', 'Layout', 'LucideIcon.razor'), 'utf8');
  for (const m of razor.matchAll(/\["([a-z0-9-]+)"\]\s*=\s*"((?:[^"\\]|\\.)*)"/g)) icons.set(m[1], m[2].replace(/\\"/g, '"'));
}
const icon = (name, cls = '') => {
  if (!icons.has(name)) { fail(`unknown icon "${name}" (the set is the one in LucideIcon.razor)`); return ''; }
  return `<svg class="icon ${cls}" viewBox="0 0 24 24" aria-hidden="true" focusable="false">${icons.get(name)}</svg>`;
};

const shotSizes = await readJson('assets', 'shots', 'manifest.json');
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

const content = {};
for (const lang of LANGS) {
  const t = await readJson('content', lang, 'site.json');
  const docs = {};
  for (const d of DOCS) docs[d] = parseFrontMatter(await read('content', lang, `${d}.md`));
  const ui = await loadStrings(lang);
  content[lang] = { t: resolveUi(t, ui, lang), docs, ui };
}

// A string in site.json may quote a button as {{ui:Some.Key}}; it is replaced by the app's own label.
function resolveUi(node, ui, lang) {
  if (typeof node === 'string') {
    return node.replace(/\{\{ui:([A-Za-z0-9_.]+)\}\}/g, (_, key) => {
      if (!ui.has(key) || !ui.translated.has(key)) { fail(`${lang}: {{ui:${key}}} is not a translated key in the app's resources`); return key; }
      return ui.get(key);
    });
  }
  if (Array.isArray(node)) return node.map((x) => resolveUi(x, ui, lang));
  if (node && typeof node === 'object') return Object.fromEntries(Object.entries(node).map(([k, v]) => [k, resolveUi(v, ui, lang)]));
  return node;
}

function parseFrontMatter(text) {
  const m = /^---\n([\s\S]*?)\n---\n/.exec(text);
  if (!m) throw new Error('missing front matter');
  const meta = Object.fromEntries(m[1].split('\n').map((l) => { const i = l.indexOf(':'); return [l.slice(0, i).trim(), l.slice(i + 1).trim()]; }));
  return { meta, body: text.slice(m[0].length) };
}

// Every language must carry the same keys and list lengths as English, so no page silently falls back.
function sameShape(a, b, path, lang) {
  if (Array.isArray(a)) {
    if (!Array.isArray(b) || a.length !== b.length) return fail(`${lang}: ${path} should be a list of ${a.length}`);
    a.forEach((x, i) => sameShape(x, b[i], `${path}[${i}]`, lang));
  } else if (a && typeof a === 'object') {
    if (!b || typeof b !== 'object') return fail(`${lang}: ${path} should be an object`);
    for (const k of Object.keys(a)) { if (!(k in b)) fail(`${lang}: missing ${path}.${k}`); else sameShape(a[k], b[k], `${path}.${k}`, lang); }
    for (const k of Object.keys(b)) if (!(k in a)) fail(`${lang}: ${path}.${k} has no English counterpart`);
  } else if (typeof b !== typeof a || (typeof b === 'string' && !b.trim())) fail(`${lang}: ${path} is empty or the wrong type`);
}
for (const lang of LANGS.filter((l) => l !== 'en')) sameShape(content.en.t, content[lang].t, 't', lang);

// Section ids in the long-form pages must match across languages, so a language switch keeps the anchor.
const idsOf = (md) => [...md.matchAll(/^#{2,3} .*\{#([\w-]+)\}\s*$/gm)].map((m) => m[1]).join(',');
for (const d of DOCS) for (const lang of LANGS.filter((l) => l !== 'en')) {
  if (idsOf(content.en.docs[d].body) !== idsOf(content[lang].docs[d].body)) fail(`${lang}/${d}.md: section ids differ from English`);
}

// ---- urls ---------------------------------------------------------------------------------------
const dirOf = (lang, slug) => `${lang === 'en' ? '' : `${lang}/`}${slug ? `${slug}/` : ''}`;
const depth = (dir) => (dir ? dir.split('/').filter(Boolean).length : 0);
const rootOf = (dir) => '../'.repeat(depth(dir));
const link = (fromDir, toDir) => (rootOf(fromDir) + toDir) || './';

// ---- markdown -----------------------------------------------------------------------------------
function markdownFor(lang, pageDir) {
  const { t, ui } = content[lang];
  const root = rootOf(pageDir);
  let toc = [];

  const shotFigure = (name, caption) => {
    const known = lang !== 'en' && shotSizes[name] && existsShot(lang, name) ? lang : 'en';
    if (!shotSizes[name]) { fail(`${lang}: unknown screenshot "${name}"`); return ''; }
    const [w, h] = shotSizes[name];
    const src = `${root}assets/shots/${known}/${name}.webp`;
    const phone = name.startsWith('phone-');
    return `<figure class="shot${phone ? ' shot-phone' : ''}"><button class="zoom" type="button" data-caption="${esc(caption)}" aria-label="${esc(t.ui.zoom)}: ${esc(caption)}"><img src="${src}" width="${w}" height="${h}" alt="${esc(caption)}" loading="lazy" decoding="async"></button><figcaption aria-hidden="true">${esc(caption)}</figcaption></figure>`;
  };

  const marked = new Marked({
    gfm: true,
    extensions: [{
      name: 'ui', level: 'inline',
      start: (src) => src.indexOf('[[ui:'),
      tokenizer(src) { const m = /^\[\[ui:([A-Za-z0-9_.]+)\]\]/.exec(src); return m ? { type: 'ui', raw: m[0], key: m[1] } : undefined; },
      renderer(tok) {
        if (!ui.has(tok.key) || !ui.translated.has(tok.key)) { fail(`${lang}: [[ui:${tok.key}]] is not a translated key in the app's resources`); return tok.key; }
        return `<span class="ui">${esc(ui.get(tok.key))}</span>`;
      },
    }],
    renderer: {
      heading({ tokens, depth: level }) {
        let html = this.parser.parseInline(tokens);
        const m = /\s*\{#([\w-]+)\}\s*$/.exec(html);
        if (m) html = html.slice(0, m.index);
        if (level <= 3) {
          if (!m) fail(`${lang}: heading "${html.replace(/<[^>]+>/g, '')}" has no {#id}`);
          else toc.push({ level, id: m[1], text: html.replace(/<[^>]+>/g, '') });
        }
        return `<h${level}${m ? ` id="${m[1]}"` : ''}>${html}</h${level}>\n`;
      },
      code({ text, lang: info }) {
        return `<div class="codeblock"><pre><code${info ? ` class="language-${esc(info)}"` : ''}>${esc(text)}</code></pre>` +
          `<button class="btn btn-small copy" type="button" data-copy data-copied="${esc(t.ui.copied)}">${icon('copy')}<span data-label>${esc(t.ui.copy)}</span></button></div>\n`;
      },
      blockquote({ tokens }) {
        const html = this.parser.parse(tokens);
        const m = /^<p>\[!(NOTE|TIP|WARNING)\](?:\s|<br>)*/.exec(html);
        if (!m) return `<blockquote>${html}</blockquote>\n`;
        const kind = m[1].toLowerCase();
        const glyph = { note: 'info', tip: 'sparkles', warning: 'alert-triangle' }[kind];
        return `<aside class="callout callout-${kind}"><p class="callout-title">${icon(glyph)}${esc(t.callouts[kind])}</p><p>${html.slice(m[0].length)}</aside>\n`;
      },
      image({ href, text }) {
        if (href.startsWith('shot:')) return shotFigure(href.slice(5), text);
        return `<img src="${esc(href)}" alt="${esc(text)}" loading="lazy">`;
      },
      paragraph({ tokens }) {
        const onlyImages = tokens.length > 0 && tokens.every((x) => x.type === 'image' || (x.type === 'text' && !x.raw.trim()));
        if (onlyImages) {
          const figures = tokens.filter((x) => x.type === 'image').map((x) => this.image(x));
          return figures.length > 1 ? `<div class="shot-pair">${figures.join('')}</div>\n` : `${figures[0]}\n`;
        }
        return `<p>${this.parser.parseInline(tokens)}</p>\n`;
      },
      link({ href, title, tokens }) {
        const text = this.parser.parseInline(tokens);
        const external = /^https?:/.test(href);
        return `<a href="${esc(href)}"${title ? ` title="${esc(title)}"` : ''}${external ? ' rel="noopener"' : ''}>${text}</a>`;
      },
    },
  });
  return { render: (md) => { toc = []; const html = marked.parse(md); return { html, toc }; } };
}

const shotFiles = new Set((await readdir(join(SRC, 'assets', 'shots'), { recursive: true })).map((f) => f.replace(/\\/g, '/')));
const existsShot = (lang, name) => shotFiles.has(`${lang}/${name}.webp`);

// ---- page shell ---------------------------------------------------------------------------------
const langNames = { en: 'English', ru: 'Русский', he: 'עברית' };
const langShort = { en: 'EN', ru: 'РУ', he: 'עב' };
const themeScript = `try{var t=localStorage.getItem('codex-site-theme');document.documentElement.setAttribute('data-theme',t==='light'?'light':'dark')}catch(e){document.documentElement.setAttribute('data-theme','dark')}`;

function shell({ lang, slug, title, description, body, jsonLd }) {
  const { t } = content[lang];
  const dir = dirOf(lang, slug);
  const root = rootOf(dir);
  const abs = (l) => `${SITE_URL}${dirOf(l, slug)}`;
  const navItems = [
    ['', 'home', t.nav.home],
    ['install', 'install', t.nav.install],
    ['guide', 'guide', t.nav.guide],
  ];
  const page = slug || 'home';
  const banners = LANGS.filter((l) => l !== lang).map((l) => {
    const b = content[l].t.banner;
    return `<div class="lang-banner" data-for="${l}" hidden lang="${l}" dir="${l === 'he' ? 'rtl' : 'ltr'}"><div class="wrap"><span>${esc(b.text)}</span><a class="btn btn-small" href="${link(dir, dirOf(l, slug))}" hreflang="${l}" data-lang-link>${esc(b.cta)}</a><button type="button">${esc(b.dismiss)}</button></div></div>`;
  }).join('');
  return `<!doctype html>
<html lang="${lang}" dir="${lang === 'he' ? 'rtl' : 'ltr'}" data-theme="dark">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(title)}</title>
<meta name="description" content="${esc(description)}">
<link rel="canonical" href="${abs(lang)}">
${LANGS.map((l) => `<link rel="alternate" hreflang="${l}" href="${abs(l)}">`).join('\n')}
<link rel="alternate" hreflang="x-default" href="${abs('en')}">
<meta property="og:type" content="website">
<meta property="og:site_name" content="TTRPG.Codex">
<meta property="og:title" content="${esc(title)}">
<meta property="og:description" content="${esc(description)}">
<meta property="og:url" content="${abs(lang)}">
<meta property="og:image" content="${SITE_URL}assets/og.png">
<meta property="og:locale" content="${t.locale}">
<meta name="twitter:card" content="summary_large_image">
<meta name="theme-color" content="#0c0a08">
<link rel="icon" href="${root}assets/icon.svg" type="image/svg+xml">
<link rel="icon" href="${root}assets/favicon.png" sizes="32x32" type="image/png">
<link rel="preload" href="${root}assets/fonts/cinzel/cinzel-latin-wght-normal.woff2" as="font" type="font/woff2" crossorigin>
<link rel="stylesheet" href="${root}assets/fonts.css">
<link rel="stylesheet" href="${root}assets/site.css">
<script>${themeScript}</script>
${jsonLd ? `<script type="application/ld+json">${JSON.stringify(jsonLd)}</script>` : ''}
</head>
<body>
<a class="skip" href="#main">${esc(t.nav.skip)}</a>
${slug === '' ? banners : ''}
<header class="site-header">
<div class="wrap header-row">
<a class="brand" href="${link(dir, dirOf(lang, ''))}" aria-label="TTRPG.Codex — ${esc(t.nav.home)}"><img src="${root}assets/icon.svg" alt="" width="36" height="36"><span class="brand-name">TTRPG.Codex</span></a>
<nav class="nav" id="site-nav" aria-label="${esc(t.nav.label)}">
${navItems.map(([s, key, label]) => `<a href="${link(dir, dirOf(lang, s))}"${page === key ? ' aria-current="page"' : ''}>${esc(label)}</a>`).join('\n')}
<a href="${REPO}" rel="noopener">${esc(t.nav.github)}</a>
</nav>
<div class="tools">
<div class="lang-switch" role="group" aria-label="${esc(t.nav.language)}">
${LANGS.map((l) => `<a href="${link(dir, dirOf(l, slug))}" lang="${l}" hreflang="${l}" data-lang-link${l === lang ? ' aria-current="true"' : ''} title="${langNames[l]}"><span aria-hidden="true">${langShort[l]}</span><span class="visually-hidden">${langNames[l]}</span></a>`).join('')}
</div>
<button class="icon-btn theme-toggle" type="button" data-theme-toggle aria-label="${esc(t.nav.theme)}"><span class="sun">${icon('sun')}</span><span class="moon">${icon('moon')}</span></button>
<button class="icon-btn menu-btn" type="button" data-menu-toggle aria-expanded="false" aria-controls="site-nav" aria-label="${esc(t.nav.menu)}">${icon('menu')}</button>
</div>
</div>
</header>
<main id="main">
${body}
</main>
${footer(lang, dir, slug)}
<dialog class="lightbox" aria-label="${esc(t.ui.zoom)}"><div class="bar"><span class="cap"></span><button class="icon-btn" type="button" data-close aria-label="${esc(t.ui.close)}">${icon('x')}</button></div><img alt=""></dialog>
<script src="${root}assets/site.js" defer></script>
</body>
</html>
`;
}

function footer(lang, dir, slug) {
  const { t } = content[lang];
  return `<footer class="site-footer"><div class="wrap">
<div class="footer-grid">
<div><p><strong class="brand-name" style="font-size:1.3rem;color:var(--text)">TTRPG.Codex</strong></p><p>${esc(t.footer.tagline)}</p><p>${esc(t.footer.license)}</p></div>
<div><h2>${esc(t.footer.project)}</h2><ul>
<li><a href="${REPO}" rel="noopener">${esc(t.footer.source)}</a></li>
<li><a href="${REPO}/releases" rel="noopener">${esc(t.footer.releases)}</a></li>
<li><a href="${REPO}/issues" rel="noopener">${esc(t.footer.issues)}</a></li>
<li><a href="${REPO}/blob/main/LEGAL.md" rel="noopener">${esc(t.footer.legal)}</a></li>
<li><a href="${REPO}/tree/main/docs" rel="noopener">${esc(t.footer.docs)}</a></li>
</ul></div>
<div><h2>${esc(t.footer.site)}</h2><ul>
<li><a href="${link(dir, dirOf(lang, 'install'))}">${esc(t.nav.install)}</a></li>
<li><a href="${link(dir, dirOf(lang, 'guide'))}">${esc(t.nav.guide)}</a></li>
${LANGS.filter((l) => l !== lang).map((l) => `<li><a href="${link(dir, dirOf(l, slug))}" lang="${l}" hreflang="${l}">${langNames[l]}</a></li>`).join('\n')}
</ul></div>
</div>
<p class="footer-base">${esc(t.footer.base)}</p>
</div></footer>`;
}

// ---- home ---------------------------------------------------------------------------------------
function homePage(lang) {
  const { t } = content[lang];
  const dir = dirOf(lang, '');
  const root = rootOf(dir);
  const shot = (name, alt, opts = {}) => {
    const useLang = existsShot(lang, name) ? lang : 'en';
    const [w, h] = shotSizes[name];
    const phone = name.startsWith('phone-');
    return `<figure class="shot${phone ? ' shot-phone' : ''}"><button class="zoom" type="button" data-caption="${esc(alt)}" aria-label="${esc(t.ui.zoom)}: ${esc(alt)}"><img src="${root}assets/shots/${useLang}/${name}.webp" width="${w}" height="${h}" alt="${esc(alt)}"${opts.eager ? ' fetchpriority="high"' : ' loading="lazy"'} decoding="async"></button></figure>`;
  };
  const guideHref = link(dir, dirOf(lang, 'guide'));
  const installHref = link(dir, dirOf(lang, 'install'));
  const arrow = `<span class="icon-flip">${icon('arrow-right')}</span>`;

  const hero = `<section class="hero"><div class="wrap hero-grid">
<div>
<p class="eyebrow">${esc(t.hero.eyebrow)}</p>
<h1>${esc(t.hero.title)}</h1>
<p class="lead">${esc(t.hero.lead)}</p>
<div class="cta-row"><a class="btn btn-primary" href="#download">${icon('download')}${esc(t.hero.primary)}</a><a class="btn" href="${guideHref}">${esc(t.hero.secondary)}${arrow}</a></div>
<p class="hero-note">${t.hero.note.split(' · ').map((part) => `<bdi>${esc(part)}</bdi>`).join(' · ')}</p>
</div>
${shot('hero', t.hero.shotAlt, { eager: true })}
</div></section>
<div class="wip"><div class="wrap">${icon('alert-triangle')}<span>${esc(t.wip)}</span></div></div>
<div class="strip"><ul>${t.strip.map((s) => `<li>${icon(s.icon)}${esc(s.text)}</li>`).join('')}</ul></div>`;

  const features = `<section class="section" id="features"><div class="wrap">
<div class="section-head"><p class="eyebrow">${esc(t.features.eyebrow)}</p><h2>${esc(t.features.title)}</h2><p class="lead">${esc(t.features.lead)}</p></div>
<div class="cards">${t.features.items.map((f) => `<article class="card"><span class="glyph">${icon(f.icon)}</span><h3>${esc(f.title)}</h3><p>${esc(f.text)}</p></article>`).join('')}</div>
</div></section>`;

  const showcase = `<section class="section section-alt" id="tour"><div class="wrap">
<div class="section-head"><p class="eyebrow">${esc(t.tour.eyebrow)}</p><h2>${esc(t.tour.title)}</h2><p class="lead">${esc(t.tour.lead)}</p></div>
<div class="showcase">
${t.tour.items.map((r, i) => `<div class="row${i % 2 ? ' flip' : ''}"><div class="row-text"><h3>${esc(r.title)}</h3><p>${esc(r.text)}</p><ul class="ticks">${r.bullets.map((b) => `<li>${esc(b)}</li>`).join('')}</ul></div>${shot(r.shot, r.alt)}</div>`).join('\n')}
<div class="phones"><div class="row-text"><h3>${esc(t.phone.title)}</h3><p>${esc(t.phone.text)}</p><ul class="ticks">${t.phone.bullets.map((b) => `<li>${esc(b)}</li>`).join('')}</ul></div>${shot('phone-table', t.phone.altTable)}${shot('phone-sheet', t.phone.altSheet)}</div>
</div>
</div></section>`;

  const ai = `<section class="section" id="ai"><div class="wrap"><div class="row">
<div class="row-text"><p class="eyebrow">${esc(t.ai.eyebrow)}</p><h2 style="font-size:clamp(1.7rem,3.4vw,2.5rem);margin-block-end:.8rem">${esc(t.ai.title)}</h2><p>${esc(t.ai.text)}</p><ul class="ticks">${t.ai.bullets.map((b) => `<li>${esc(b)}</li>`).join('')}</ul></div>
${shot('ai-settings', t.ai.alt)}
</div></div></section>`;

  const tongues = `<section class="section section-alt" id="languages"><div class="wrap">
<div class="section-head center"><p class="eyebrow">${esc(t.languages.eyebrow)}</p><h2>${esc(t.languages.title)}</h2><p class="lead">${esc(t.languages.lead)}</p></div>
<div class="tongues">${LANGS.map((l) => {
    const [w, h] = shotSizes.table;
    return `<figure class="shot"><button class="zoom" type="button" data-caption="${esc(t.languages.alts[l])}" aria-label="${esc(t.ui.zoom)}: ${esc(langNames[l])}"><img src="${root}assets/shots/${l}/table.webp" width="${w}" height="${h}" alt="${esc(t.languages.alts[l])}" loading="lazy" decoding="async"></button><figcaption lang="${l}">${langNames[l]}</figcaption></figure>`;
  }).join('')}</div>
<p class="lead" style="margin:2rem auto 0;text-align:center">${esc(t.languages.note)}</p>
</div></section>`;

  const steps = `<section class="section" id="how"><div class="wrap">
<div class="section-head"><p class="eyebrow">${esc(t.how.eyebrow)}</p><h2>${esc(t.how.title)}</h2></div>
<ol class="steps">${t.how.steps.map((s) => `<li><h3>${esc(s.title)}</h3><p>${esc(s.text)}</p></li>`).join('')}</ol>
</div></section>`;

  const systems = `<section class="section section-alt" id="systems"><div class="wrap">
<div class="section-head"><p class="eyebrow">${esc(t.systems.eyebrow)}</p><h2>${esc(t.systems.title)}</h2><p class="lead">${esc(t.systems.lead)}</p></div>
<div class="two-col">${t.systems.panels.map((p) => `<div class="panel"><h3>${esc(p.title)}</h3><p>${esc(p.text)}</p>${p.link ? `<p><a href="${p.link.href}" rel="noopener">${esc(p.link.label)}</a></p>` : ''}</div>`).join('')}</div>
</div></section>`;

  const releases = `${REPO}/releases/latest`;
  const download = `<section class="section" id="download" data-release="https://api.github.com/repos/myarichuk/TTRPG.Codex/releases/latest"><div class="wrap">
<div class="section-head"><p class="eyebrow">${esc(t.download.eyebrow)}</p><h2>${esc(t.download.title)}</h2><p class="lead">${esc(t.download.lead)}</p></div>
<p class="release-line" hidden><span>${esc(t.download.latest)}</span><span data-release-version></span><span class="muted">${esc(t.download.published)} <span data-release-date></span></span></p>
<div class="platforms">${t.download.platforms.map((p) => `<div class="platform" data-os="${p.os}"><span class="pill" hidden>${esc(t.download.recommended)}</span><h3>${esc(p.name)}</h3><p class="sub">${esc(p.sub)}</p><div class="actions"><a class="btn btn-primary" href="${releases}" data-asset="${p.primary.asset}">${icon('download')}${esc(p.primary.label)}</a><a class="btn" href="${releases}" data-asset="${p.secondary.asset}">${esc(p.secondary.label)}</a></div></div>`).join('')}</div>
<p class="muted" style="margin-block-start:1.5rem">${esc(t.download.unsigned)} <a href="${installHref}">${esc(t.download.installLink)}</a> · <a href="${REPO}/releases" rel="noopener">${esc(t.download.allReleases)}</a> · <a href="${installHref}#source">${esc(t.download.fromSource)}</a></p>
</div></section>`;

  const jsonLd = {
    '@context': 'https://schema.org', '@type': 'SoftwareApplication', name: 'TTRPG.Codex', applicationCategory: 'GameApplication',
    operatingSystem: 'Windows, macOS, Linux', description: t.meta.description, url: `${SITE_URL}${dir}`, inLanguage: lang,
    offers: { '@type': 'Offer', price: '0', priceCurrency: 'USD' }, license: 'https://polyformproject.org/licenses/noncommercial/1.0.0', image: `${SITE_URL}assets/og.png`,
  };
  return shell({ lang, slug: '', title: t.meta.title, description: t.meta.description, jsonLd, body: [hero, features, showcase, ai, tongues, steps, systems, download].join('\n') });
}

// ---- documentation pages ------------------------------------------------------------------------
function docPage(lang, slug) {
  const { t, docs } = content[lang];
  const dir = dirOf(lang, slug);
  const { meta, body } = docs[slug];
  const { html, toc } = markdownFor(lang, dir).render(body);
  const tocHtml = (cls) => `<ol>${toc.map((h) => `<li${h.level === 3 ? ' class="sub"' : ''}><a href="#${h.id}">${h.text}</a></li>`).join('')}</ol>`;
  const order = DOCS;
  const i = order.indexOf(slug);
  const neighbour = (j, label, cls) => (order[j] ? `<a href="${link(dir, dirOf(lang, order[j]))}"${cls}><small>${esc(label)}</small>${esc(docs[order[j]].meta.title)}</a>` : '<span></span>');
  const bodyHtml = `<div class="doc-head"><div class="wrap"><p class="eyebrow">${esc(t.nav[slug])}</p><h1>${esc(meta.title)}</h1><p class="lead">${esc(meta.lead)}</p></div></div>
<div class="wrap doc-layout">
<aside class="toc" aria-label="${esc(t.ui.toc)}"><h2>${esc(t.ui.toc)}</h2>${tocHtml()}</aside>
<article class="prose">
<details class="toc-mobile"><summary>${esc(t.ui.toc)}</summary>${tocHtml()}</details>
${html}
<div class="doc-nav">${neighbour(i - 1, t.ui.previous)}${neighbour(i + 1, t.ui.next)}</div>
<p class="muted" style="margin-block-start:2rem"><a href="${REPO}/edit/main/site/src/content/${lang}/${slug}.md" rel="noopener">${esc(t.ui.edit)}</a></p>
</article>
</div>`;
  return shell({ lang, slug, title: `${meta.title} — TTRPG.Codex`, description: meta.description, body: bodyHtml });
}

// ---- write --------------------------------------------------------------------------------------
await rm(DIST, { recursive: true, force: true });
await cp(join(SRC, 'assets'), join(DIST, 'assets'), { recursive: true });
const write = async (rel, text) => { const f = join(DIST, rel); await mkdir(dirname(f), { recursive: true }); await writeFile(f, text); };

const urls = [];
for (const lang of LANGS) {
  await write(`${dirOf(lang, '')}index.html`, homePage(lang));
  urls.push('');
  for (const slug of DOCS) await write(`${dirOf(lang, slug)}index.html`, docPage(lang, slug));
}
const slugs = ['', ...DOCS];
await write('sitemap.xml', `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">
${slugs.flatMap((s) => LANGS.map((l) => `<url><loc>${SITE_URL}${dirOf(l, s)}</loc>${LANGS.map((a) => `<xhtml:link rel="alternate" hreflang="${a}" href="${SITE_URL}${dirOf(a, s)}"/>`).join('')}<xhtml:link rel="alternate" hreflang="x-default" href="${SITE_URL}${dirOf('en', s)}"/></url>`)).join('\n')}
</urlset>
`);
await write('robots.txt', `User-agent: *\nAllow: /\nSitemap: ${SITE_URL}sitemap.xml\n`);
await write('.nojekyll', '');
await write('404.html', `<!doctype html><html lang="en" dir="ltr" data-theme="dark"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>404 — TTRPG.Codex</title><meta name="description" content="This page could not be found. TTRPG.Codex is a self-hosted companion for tabletop role-playing campaigns."><link rel="stylesheet" href="${new URL(SITE_URL).pathname}assets/fonts.css"><link rel="stylesheet" href="${new URL(SITE_URL).pathname}assets/site.css"><script>${themeScript}</script></head><body><main class="wrap" style="padding-block:6rem;text-align:center"><h1>404</h1><p class="lead" style="margin-inline:auto">This page wandered off the map.<br><span lang="ru">Эта страница ушла с карты.</span> · <span lang="he" dir="rtl">הדף הזה יצא מהמפה.</span></p><p><a class="btn btn-primary" href="${new URL(SITE_URL).pathname}">TTRPG.Codex</a></p></main></body></html>`);

if (problems.length) {
  console.error(`\n${problems.length} problem(s):\n${[...new Set(problems)].map((p) => `  - ${p}`).join('\n')}\n`);
  process.exit(1);
}
console.log(`Built ${LANGS.length * slugs.length} pages into ${DIST}`);
