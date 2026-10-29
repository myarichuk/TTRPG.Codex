#!/usr/bin/env node
// Takes the website's screenshots from the real app, in every language.
//
//   dotnet run --project src/Codex.Web with Codex__EnableTestSeedEndpoint=true and a throwaway
//   Codex__DataDirectory (see site/README.md), then:
//
//   node site/tools/capture-screenshots.mjs --base http://127.0.0.1:5199 [--out site/.capture] [--langs en,ru,he]
//
// Writes PNGs; tools/optimize-screenshots.py turns them into the WebP files the site ships.
// It seeds users and characters through the app's own test endpoints (the same ones the Playwright
// suite uses), drives everything else through the UI, and writes <out>/<lang>/<name>.png.
import { mkdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { languages, party } from './demo-data.mjs';
import { loadStrings } from './resx.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith('--') ? [...acc, [a.slice(2), all[i + 1]]] : acc), []));
const BASE = args.base ?? 'http://127.0.0.1:5199';
const OUT = resolve(args.out ?? join(here, '..', '.capture'));
const LANGS = (args.langs ?? 'en,ru,he').split(',');
const PASSWORD = 'Correct-Horse-1';

const KIND = { pc: 0, npc: 1, monster: 2 };
const VIS = { Hidden: 0, Unknown: 1, Known: 2 };

const post = async (path, body) => {
  const res = await fetch(BASE + path, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  if (!res.ok) throw new Error(`${path}: ${res.status} ${await res.text()}`);
};

const interactive = (page) => page.locator('[data-interactive]').waitFor({ state: 'attached', timeout: 30_000 });
const settle = (page, ms = 700) => page.waitForTimeout(ms);

async function login(page, username) {
  await page.goto('/login');
  await page.locator('#username').fill(username);
  await page.locator('#password').fill(PASSWORD);
  await page.locator('form:has(#username) button[type=submit]').click();
  await page.waitForURL((u) => !u.toString().includes('/login'));
  await interactive(page);
}

async function setCulture(page, tag, returnUrl) {
  await page.goto(`/set-culture?culture=${tag}&returnUrl=${encodeURIComponent(returnUrl)}`);
  // The sign-in page is rendered statically, so there is no circuit to wait for there.
  if (returnUrl !== '/login') await interactive(page);
}

async function shoot(page, lang, name, { fullPage = false } = {}) {
  const file = join(OUT, lang, `${name}.png`);
  await mkdir(dirname(file), { recursive: true });
  await settle(page);
  await page.screenshot({ path: file, fullPage });
  console.log('  ', lang, name);
}

async function captureLanguage(browser, lang) {
  const d = languages[lang];
  const ui = Object.fromEntries(await loadStrings(lang));
  const run = `${lang}-${Date.now().toString(36)}`;
  const userIds = { dm: `dm-${run}`, players: d.players.map((_, i) => `pl${i}-${run}`) };
  // Usernames carry the language so the header shows a name in the right script; they are unique per run.
  const names = { dm: `${d.dm}`, players: d.players };
  await post('/test/seed/users', [
    { id: userIds.dm, username: names.dm, password: PASSWORD, roles: ['Player'] },
    ...names.players.map((n, i) => ({ id: userIds.players[i], username: n, password: PASSWORD, roles: ['Player'] })),
  ]);

  // 1100 px wide: shown at about 650 px on the page, so the app's own text stays readable.
  const desktop = { width: 1100, height: 820 };
  const dmCtx = await browser.newContext({ baseURL: BASE, viewport: desktop, locale: lang, deviceScaleFactor: 1 });
  const dm = await dmCtx.newPage();
  await setCulture(dm, d.tag, '/login');
  await login(dm, names.dm);

  // ---- campaign ----
  await dm.goto('/');
  await interactive(dm);
  await dm.getByTestId('new-campaign').click();
  await dm.getByTestId('new-campaign-name').fill(d.campaign.name);
  await dm.getByTestId('new-campaign-system').selectOption('srd5e');
  await dm.getByTestId('new-campaign-create').click();
  await dm.waitForURL((u) => /\/campaigns\/[^/]+$/.test(u.pathname) && !u.pathname.endsWith('/campaigns/'));
  await dm.getByTestId('campaign-title').waitFor();
  const campaignId = new URL(dm.url()).pathname.split('/')[2];
  console.log(lang, 'campaign', campaignId);

  // tagline and description live in Settings
  await dm.goto(`/campaigns/${campaignId}/settings`);
  await interactive(dm);
  await dm.getByTestId('settings-tagline').fill(d.campaign.tagline);
  await dm.getByTestId('settings-description').fill(d.campaign.description);
  await dm.getByTestId('settings-description').press('Tab');
  await settle(dm, 300);
  await dm.getByTestId('settings-save').click();
  await settle(dm, 800);

  // ---- people ----
  const actors = [];
  party.forEach((p, i) => actors.push({
    id: `${p.id}-${run}`, campaignId, name: d.pcs[p.id], kind: KIND.pc, ownerUserId: userIds.players[p.player], visibility: VIS.Known,
    hp: p.hp[0], hpMax: p.hp[1],
    sheet: { class: p.cls, species: p.species, background: p.background, str: p.scores[0], dex: p.scores[1], con: p.scores[2], int: p.scores[3], wis: p.scores[4], cha: p.scores[5], level: p.level, armorClass: p.ac, speed: 30, alignment: p.alignment },
  }));
  d.npcs.forEach((n, i) => actors.push({ id: `npc${i}-${run}`, campaignId, name: n.name, kind: KIND.npc, visibility: VIS[n.visibility], hp: n.hp[0], hpMax: n.hp[1], sheet: { level: 3 } }));
  d.monsters.forEach((m, i) => {
    for (let c = 0; c < m.count; c++) {
      actors.push({ id: `mon${i}-${c}-${run}`, campaignId, name: m.count > 1 ? `${m.name} ${c + 1}` : m.name, kind: KIND.monster, visibility: VIS.Known, hp: m.hp[0], hpMax: m.hp[1], sheet: {} });
    }
  });
  await post('/test/seed/actors', actors);

  // players join through the invite link, like real players
  await dm.goto(`/campaigns/${campaignId}`);
  await interactive(dm);
  const invite = await dm.getByTestId('invite-link').inputValue();
  const playerPages = [];
  for (let i = 0; i < d.players.length; i++) {
    const ctx = await browser.newContext({ baseURL: BASE, viewport: desktop, locale: lang });
    const page = await ctx.newPage();
    await setCulture(page, d.tag, '/login');
    await login(page, names.players[i]);
    await page.goto(invite.replace(/^https?:\/\/[^/]+/, BASE));
    await page.waitForLoadState('networkidle');
    playerPages.push({ ctx, page });
  }

  const section = async (page, s) => { await page.goto(`/campaigns/${campaignId}/${s}`); await interactive(page); };

  // ---- world: places, wiki, lore, a session ----
  await section(dm, 'world');
  await dm.getByTestId('new-region-name').fill(d.region);
  await dm.getByTestId('new-region-add').click();
  const placeInput = dm.locator('input[id^="loc-"]:not([id^="loc-parent"]):not([id^="loc-edit"])').first();
  await placeInput.waitFor();
  for (const place of d.places) {
    await dm.locator('input[id^="loc-"]:not([id^="loc-parent"]):not([id^="loc-edit"])').first().fill(place.name);
    await dm.locator('input[id^="loc-"]:not([id^="loc-parent"]):not([id^="loc-edit"])').first().press('Enter');
    await dm.getByTestId('location-row').filter({ hasText: place.name }).waitFor();
  }

  for (const reveal = dm.getByTestId('location-row').locator('button', { hasText: ui['Campaign.World.Reveal'] }); await reveal.count() > 0;) {
    await reveal.first().click();
    await settle(dm, 300);
  }

  // Every place already has a wiki page of its own: write into those, as a DM would.
  for (const [title, body] of [[d.wiki.second, d.wiki.secondBody], [d.wiki.title, d.wiki.body]]) {
    await section(dm, 'wiki');
    await dm.getByTestId('wiki-list').locator('a').filter({ hasText: title }).first().click();
    await dm.getByTestId('wiki-page').waitFor();
    await dm.getByTestId('wiki-edit').click();
    await dm.getByTestId('wiki-body').waitFor();
    await dm.getByTestId('wiki-body').fill(body);
    await dm.getByTestId('wiki-body').press('Tab'); // blur: the editor commits its text on change
    await settle(dm, 300);
    await dm.getByTestId('wiki-visibility').selectOption('Party');
    await dm.getByTestId('wiki-save').click();
    await dm.getByTestId('wiki-page').waitFor();
    await settle(dm, 400);
  }

  await section(dm, 'lore');
  await dm.getByTestId('new-fact-summary').fill(d.fact);
  await dm.getByTestId('new-fact-add').click();
  await dm.getByTestId('fact-card').first().waitFor();

  await section(dm, 'sessions');
  await dm.getByTestId('new-session-title').fill(d.session.title);
  await dm.getByTestId('new-session-add').click();
  const sessionLink = dm.getByTestId('session-list').getByRole('link', { name: d.session.title });
  await sessionLink.waitFor();
  const sessionPath = await sessionLink.getAttribute('href');
  await dm.goto(sessionPath);
  await interactive(dm);
  await dm.getByTestId('session-recap').fill(d.session.recap);
  await dm.getByRole('button', { name: ui['Session.SaveRecap'] }).first().click();
  await settle(dm, 900);

  // ---- live table ----
  await section(dm, 'table');
  await dm.getByTestId('start-table').click();
  await dm.getByTestId('initiative-header').waitFor();
  const sailor = (n) => `${d.monsters[0].name} ${n}`;
  const initiative = [[d.pcs.aria, 21], [sailor(1), 18], [d.pcs.bram, 17], [d.monsters[1].name, 15], [d.pcs.ione, 13], [sailor(2), 11], [d.pcs.veyra, 9], [sailor(3), 6]];
  for (const [name, roll] of initiative) {
    const select = dm.getByTestId('add-to-initiative-select');
    await select.locator('option', { hasText: name }).first().waitFor({ state: 'attached', timeout: 20_000 });
    await select.selectOption({ label: name });
    await dm.getByTestId('manual-initiative-input').fill(String(roll));
    await dm.getByTestId('set-initiative-btn').click();
    await settle(dm, 250);
  }
  for (const [who, [condition, rounds]] of Object.entries(d.conditions)) {
    const row = dm.locator(`[data-testid='participant-row'][data-actor-id='${who}-${run}']`);
    await row.getByTestId('select-participant').click();
    await row.getByTestId('condition-input').fill(condition);
    await row.getByTestId('condition-rounds-input').fill(String(rounds));
    await row.getByTestId('condition-add-btn').click();
    await settle(dm, 300);
  }
  // collapse the command panel again so the shot shows the order, not a form
  await dm.locator(`[data-testid='participant-row'][data-actor-id='bram-${run}']`).getByTestId('select-participant').click().catch(() => {});
  await dm.evaluate(() => window.scrollTo(0, 0));

  const goTop = (page) => page.evaluate(() => window.scrollTo(0, 0));

  // ---- DM desktop shots ----
  await dm.setViewportSize({ width: 1100, height: 900 });
  await goTop(dm);
  await shoot(dm, lang, 'table');
  // the hero is shown smaller still, so it gets a tighter crop of the same screen
  await dm.setViewportSize({ width: 1024, height: 720 });
  await goTop(dm);
  await shoot(dm, lang, 'hero');
  await dm.setViewportSize({ width: 1100, height: 900 });

  await dm.evaluate(() => localStorage.setItem('codex-theme', 'light'));
  await dm.reload(); await interactive(dm); await goTop(dm);
  await shoot(dm, lang, 'table-light');
  await dm.evaluate(() => localStorage.removeItem('codex-theme'));
  await dm.reload(); await interactive(dm);
  await dm.setViewportSize({ width: 1100, height: 820 });

  const visit = async (name, path, prepare) => {
    await dm.goto(path.startsWith('/') ? path : `/campaigns/${campaignId}/${path}`);
    await interactive(dm);
    if (prepare) await prepare();
    await goTop(dm);
    await shoot(dm, lang, name);
  };
  await visit('home', '/');
  await visit('overview', '');
  await visit('cast', 'cast');
  await dm.setViewportSize({ width: 1100, height: 1250 });
  await visit('sheet', `actors/aria-${run}`);
  await dm.setViewportSize({ width: 1100, height: 820 });
  await visit('bestiary', 'bestiary');
  await visit('places', 'world');
  await visit('wiki-page', 'wiki', async () => { await dm.getByTestId('wiki-list').locator('a').filter({ hasText: d.wiki.title }).first().click(); await dm.getByTestId('wiki-page').waitFor(); });
  await visit('lore', 'lore');
  await visit('session', sessionPath);
  await visit('library', 'library/monster/goblin-boss');
  await visit('settings', 'settings');
  await visit('ai-settings', '/settings/ai');

  // ---- what a player sees, on a phone ----
  const phone = await browser.newContext({
    baseURL: BASE, viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, locale: lang,
  });
  const pp = await phone.newPage();
  await setCulture(pp, d.tag, '/login');
  await login(pp, names.players[0]);
  await pp.goto(`/campaigns/${campaignId}/table`); await interactive(pp);
  await pp.getByTestId('initiative-header').waitFor();
  await shoot(pp, lang, 'phone-table');
  await pp.goto(`/campaigns/${campaignId}/actors/aria-${run}`); await interactive(pp);
  await shoot(pp, lang, 'phone-sheet');
  await phone.close();

  for (const { ctx } of playerPages) await ctx.close();
  await dmCtx.close();
}

const browser = await chromium.launch();
try {
  for (const lang of LANGS) await captureLanguage(browser, lang);
} finally {
  await browser.close();
}
