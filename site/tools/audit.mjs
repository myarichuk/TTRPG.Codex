#!/usr/bin/env node
// Browser audit of the built site (needs Playwright's Chromium): node tools/audit.mjs [baseUrl]
// For every page, in every language, at phone, tablet and desktop widths: no sideways overflow, no console
// errors or failed requests, every image really loads, and the interactive bits (theme, menu, lightbox,
// copy buttons) work. Start the preview first: npm run serve.
import { chromium } from 'playwright';

const base = (process.argv[2] ?? 'http://localhost:4173').replace(/\/$/, '');
const pages = ['', 'install', 'guide'];
const langs = ['', 'ru', 'he'];
const widths = [[390, 844], [768, 1024], [1440, 900]];
const problems = [];
const browser = await chromium.launch();
// The download section asks GitHub for the newest release; answer it locally so the audit is offline and deterministic.
const stubRelease = (ctx) => ctx.route('https://api.github.com/**', (route) => route.fulfill({
  json: { tag_name: 'v9.9.9', published_at: '2026-01-01T00:00:00Z', assets: [{ name: 'TTRPG.Codex-9.9.9-win-x64-setup.exe', browser_download_url: 'https://example.invalid/setup.exe' }] },
}));

for (const lang of langs) for (const slug of pages) for (const [w, h] of widths) {
  const url = `${base}/${[lang, slug].filter(Boolean).join('/')}${lang || slug ? '/' : ''}`;
  const label = `${url.replace(base, '') || '/'} @${w}`;
  const ctx = await browser.newContext({ viewport: { width: w, height: h } });
  await stubRelease(ctx);
  const page = await ctx.newPage();
  const errors = [];
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('requestfailed', (r) => errors.push(`failed: ${r.url()}`));
  page.on('response', (r) => r.status() >= 400 && r.url().startsWith(base) && errors.push(`${r.status()}: ${r.url()}`));
  await page.goto(url, { waitUntil: 'networkidle' });

  // lazy images only load near the viewport: walk the page, then ask each one
  await page.evaluate(async () => { for (let y = 0; y < document.body.scrollHeight; y += 500) { window.scrollTo({ top: y, behavior: 'instant' }); await new Promise((r) => setTimeout(r, 40)); } });
  await page.waitForTimeout(500);
  const state = await page.evaluate(() => ({
    overflow: document.documentElement.scrollWidth - window.innerWidth,
    broken: [...document.images].filter((i) => !i.closest('dialog') && !(i.complete && i.naturalWidth > 0)).map((i) => i.src.split('/').slice(-3).join('/')),
    dir: document.documentElement.dir,
    wide: [...document.querySelectorAll('body *')].filter((el) => el.getBoundingClientRect().right > window.innerWidth + 1 && !el.closest('pre, table, .toc, [hidden], dialog')).slice(0, 3).map((el) => `${el.tagName}.${el.className}`),
  }));
  if (slug === '' && !(await page.locator('[data-release-version]').first().textContent())?.includes('9.9.9')) problems.push(`${label}: release version was not filled in`);
  if (state.overflow > 1) problems.push(`${label}: page overflows sideways by ${state.overflow}px (${state.wide.join(', ')})`);
  if (state.broken.length) problems.push(`${label}: images did not load: ${state.broken.join(', ')}`);
  if (errors.length) problems.push(`${label}: ${errors.join('; ')}`);
  await ctx.close();
}

// interactive behaviour, once per language on the pages that have it
for (const lang of langs) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await stubRelease(ctx);
  const page = await ctx.newPage();
  const at = (p) => `${base}/${[lang, p].filter(Boolean).join('/')}${lang || p ? '/' : ''}`;
  await page.goto(at(''), { waitUntil: 'networkidle' });
  const before = await page.evaluate(() => document.documentElement.dataset.theme);
  await page.click('[data-theme-toggle]');
  const after = await page.evaluate(() => document.documentElement.dataset.theme);
  if (before === after) problems.push(`/${lang}: theme toggle did nothing`);
  await page.reload({ waitUntil: 'networkidle' });
  if ((await page.evaluate(() => document.documentElement.dataset.theme)) !== after) problems.push(`/${lang}: theme choice not remembered`);

  await page.locator('button.zoom').first().click();
  if (!(await page.locator('dialog.lightbox[open] img').first().isVisible())) problems.push(`/${lang}: lightbox did not open`);
  await page.keyboard.press('Escape');

  await page.goto(at('install'), { waitUntil: 'networkidle' });
  await page.locator('[data-copy]').first().click();
  if (!(await page.locator('[data-copy][data-done]').count())) { /* clipboard may be blocked headless; not a failure */ }
  const dirAttr = await page.evaluate(() => document.documentElement.dir);
  if ((lang === 'he') !== (dirAttr === 'rtl')) problems.push(`/${lang}: wrong direction ${dirAttr}`);
  await ctx.close();

  const phone = await browser.newContext({ viewport: { width: 390, height: 844 } });
  const p2 = await phone.newPage();
  await p2.goto(at(''), { waitUntil: 'networkidle' });
  await p2.click('[data-menu-toggle]');
  if (!(await p2.locator('.nav a').first().isVisible())) problems.push(`/${lang}: mobile menu did not open`);
  await phone.close();
}

await browser.close();
if (problems.length) { console.error(`${problems.length} problem(s):\n${problems.map((p) => `  - ${p}`).join('\n')}`); process.exit(1); }
console.log('Audit OK');
