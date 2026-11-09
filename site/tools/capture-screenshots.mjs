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
import { mkdir, writeFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
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
const PY = args.python ?? 'python3'; // needs Pillow (and poppler's pdftoppm for the printed-pages shot)
// --book-only: set the demo campaign up and take only the book's screens (the rest are unchanged, so rerunning them is wasted time).
const BOOK_ONLY = 'book-only' in args || process.argv.includes('--book-only');

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
  const dmCtx = await browser.newContext({ baseURL: BASE, viewport: desktop, locale: lang, colorScheme: 'dark', deviceScaleFactor: 1 });
  const dm = await dmCtx.newPage();
  // Leaving an editor with unsaved text asks first; the script always means to leave.
  dm.on('dialog', (dialog) => dialog.accept());
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
  d.npcs.forEach((n, i) => actors.push({
    id: `npc${i}-${run}`, campaignId, name: n.name, kind: KIND.npc, visibility: VIS[n.visibility], hp: n.hp[0], hpMax: n.hp[1], sheet: { level: 3 },
    publicName: n.publicName, publicDescription: n.publicDescription,
    // The first NPC carries a note meant for the first character only, so "PDF for a player" has something to add.
    biography: i === 0 ? `${n.note}\n\n:::for [[actor:aria-${run}]]\n${d.forNote}\n:::` : undefined,
  }));
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
    const ctx = await browser.newContext({ baseURL: BASE, viewport: desktop, locale: lang, colorScheme: 'dark' });
    const page = await ctx.newPage();
    await setCulture(page, d.tag, '/login');
    await login(page, names.players[i]);
    await page.goto(invite.replace(/^https?:\/\/[^/]+/, BASE));
    await page.waitForLoadState('networkidle');
    playerPages.push({ ctx, page });
  }

  // The getting-started list, half done: described, invited and cast, but no page, place or session yet.
  await dm.goto(`/campaigns/${campaignId}`);
  await interactive(dm);
  await dm.getByTestId('setup-checklist').waitFor();
  await shoot(dm, lang, 'setup');

  const goTop = (page) => page.evaluate(() => window.scrollTo(0, 0));
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

  // A page of each of the other kinds, so the list has something to filter and "[[" something to offer.
  for (const p of d.pages) {
    await dm.goto(`/campaigns/${campaignId}/wiki/new?title=${encodeURIComponent(p.title)}`);
    await interactive(dm);
    await dm.getByTestId('wiki-kind-select').selectOption(p.kind);
    await dm.getByTestId('wiki-body').fill(p.body);
    await dm.getByTestId('wiki-body').press('Tab');
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

  if (!BOOK_ONLY) {
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
  await dm.setViewportSize({ width: 1100, height: 1300 });
  await visit('cast', 'cast');
  await dm.setViewportSize({ width: 1100, height: 820 });
  await dm.setViewportSize({ width: 1100, height: 1250 });
  await visit('sheet', `actors/aria-${run}`);
  await dm.setViewportSize({ width: 1100, height: 820 });
  await visit('bestiary', 'bestiary');
  await visit('places', 'world');
  const openHarbour = async () => { await dm.getByTestId('wiki-list').locator('a').filter({ hasText: d.wiki.title }).first().click(); await dm.getByTestId('wiki-page').waitFor(); };
  await visit('wiki-list', 'wiki');
  // The DM's view: the secret and what players are told instead, and the card a link shows on hover.
  await dm.goto(`/campaigns/${campaignId}/wiki`);
  await interactive(dm);
  await openHarbour();
  await goTop(dm);
  await dm.getByTestId('wiki-body').locator('a.wiki-link').first().hover();
  await dm.getByTestId('hover-card').waitFor();
  await shoot(dm, lang, 'wiki-page');
  await dm.mouse.move(0, 0);
  // Writing: "[[" offers the pages, and the save bar says there is something to save.
  await dm.getByTestId('wiki-edit').click();
  // The page's text and the editor share the test id: wait for the textarea.
  const editor = dm.locator('textarea[data-testid=wiki-body]');
  await editor.waitFor();
  // Mid-text, after the line that names the harbourmaster, so the suggestions open over the text and not the save bar.
  await editor.evaluate((el) => {
    const at = el.value.indexOf('\n', el.value.indexOf('@'));
    el.focus();
    el.setSelectionRange(at, at);
    el.scrollTop = 0;
  });
  await dm.evaluate(() => document.querySelector('.md-toolbar, [data-testid=wiki-body]')?.scrollIntoView({ block: 'start' }));
  await editor.pressSequentially(' [[');
  await dm.locator('#mention-list').waitFor();
  await shoot(dm, lang, 'wiki-edit');
  await editor.press('Escape');
  await visit('lore', 'lore');
  await visit('session', sessionPath);
  await visit('library', 'library/monster/goblin-boss');
  await visit('settings', 'settings');
  await visit('ai-settings', '/settings/ai');

  }

  // ---- the book: print panel, pictures, the editor ----
  // Nothing here asks a model or draws a picture, and no key is ever entered: the AI and picture screens are shown as they
  // are before a run (the "set up a model" notes and the choices), and the editor's Ask AI bar is only ever in view, never used.
  {
    await dm.evaluate(() => localStorage.removeItem('codex-theme')); // the app is dark unless the reader chose otherwise
    const art = `${OUT}-art`;
    execFileSync(PY, [join(here, 'demo-art.py'), art], { stdio: 'inherit' });
    const upload = async (path, file) => {
      await dm.goto(path); await interactive(dm);
      await dm.locator('input[type=file]').first().setInputFiles(join(art, file));
      await settle(dm, 1400);
    };
    await upload(`/campaigns/${campaignId}/settings`, 'cover.png'); // the campaign cover goes on the book's title page
    await upload(`/campaigns/${campaignId}/actors/aria-${run}`, 'aria.png');
    await upload(`/campaigns/${campaignId}/actors/npc0-${run}`, 'odessa.png');
    await upload(`/campaigns/${campaignId}/actors/npc1-${run}`, 'bell-ringer.png');

    const openBook = async (height = 1150) => {
      await dm.setViewportSize({ width: 1100, height });
      await dm.goto(`/campaigns/${campaignId}/settings`); await interactive(dm);
      await dm.getByTestId('book-edition').selectOption('party');
      await settle(dm, 400);
      console.log('  theme', await dm.evaluate(() => [document.documentElement.dataset.theme, localStorage.getItem('codex-theme')]).catch(() => '?'));
    };
    const panelTop = async () => {
      // The panel's top just under the page header, like every other screen shot.
      await dm.getByTestId('book-panel').evaluate((el) => el.scrollIntoView({ block: 'start', behavior: 'instant' }));
      await dm.evaluate(() => window.scrollBy({ top: -78, behavior: 'instant' }));
    };

    // Print tab: the party edition in the Parchment look with the preview open.
    await openBook();
    await dm.getByTestId('book-theme').selectOption('parchment');
    await settle(dm, 500);
    await dm.getByTestId('book-preview-toggle').click();
    await dm.getByTestId('book-preview').waitFor();
    await settle(dm, 2500);
    await panelTop();
    await shoot(dm, lang, 'book-panel');

    // The printed party book, taken from the real route, and some of its pages laid side by side.
    const pdfRes = await dmCtx.request.get(`${BASE}/campaigns/${campaignId}/book/party/pdf?lang=${lang}&size=A4&cols=2&pics=1&theme=parchment`, { timeout: 120_000 });
    if (!pdfRes.ok()) throw new Error(`book pdf: ${pdfRes.status()}`);
    const pdfFile = join(`${OUT}-art`, `party-${lang}.pdf`);
    await writeFile(pdfFile, await pdfRes.body());
    try { execFileSync(PY, [join(here, 'book-pages.py'), pdfFile, join(OUT, lang, 'book-pages.png'), '1', '4', '6'], { stdio: 'inherit' }); } catch { console.warn('  book-pages skipped (needs poppler pdftoppm)'); }

    // Pictures tab: the tray with its slots, the art director's choices and the drawing choices, before any run.
    await openBook(1500);
    await dm.getByTestId('book-tab-art').click();
    // The tray opens by itself the first time its tab is shown; open it by hand only if it did not.
    if (!(await dm.getByTestId('book-art-director').waitFor({ timeout: 5000 }).then(() => true, () => false))) await dm.getByTestId('book-art-toggle').click();
    await dm.getByTestId('book-art-director').waitFor();
    await settle(dm, 1500);
    await panelTop();
    // Several versions of one picture: two pictures put in the first place, so "Versions" has something to show.
    const firstFile = dm.locator('[data-testid="book-art-slots"] input[type=file]').first();
    await firstFile.setInputFiles(join(art, 'cover.png'));
    await settle(dm, 1800);
    await firstFile.setInputFiles(join(art, 'odessa.png'));
    await settle(dm, 1800);
    await dm.locator('[data-testid$="-versions-toggle"]').first().click();
    await dm.getByTestId('book-art-version').first().waitFor();
    await settle(dm, 1000);
    await panelTop();
    await shoot(dm, lang, 'book-art');

    // The editor, party edition.
    await openBook(900);
    await dm.getByTestId('book-tab-draft').click();
    await dm.getByTestId('book-draft-open').click();
    const dialog = dm.getByTestId('book-draft-dialog');
    await dialog.waitFor();
    await dm.getByTestId('book-draft-preview').waitFor();
    await settle(dm, 3000);
    const sections = await dm.getByTestId('book-draft-chapter').evaluateAll((els) => els.map((e) => e.getAttribute('data-section')));
    console.log('  chapters', sections.join(','));
    const chapter = (name) => dm.locator(`[data-testid=book-draft-chapter][data-section="${name}"]`);
    const type = async (text) => {
      await dm.locator('[data-testid=book-draft-code] .cm-content').click();
      await dm.keyboard.press('ControlOrMeta+End');
      await dm.keyboard.type(text);
      await settle(dm, 1800);
    };
    await chapter(sections.find((x) => /cast/i.test(x)) ?? sections[1]).click();
    await settle(dm, 2000);
    await shoot(dm, lang, 'book-editor');

    // More: one copy of the party book for each player who owns a character (nothing is downloaded here).
    await dm.getByTestId('book-draft-more').click();
    await dm.getByTestId('book-draft-for-player').first().waitFor();
    await settle(dm, 500);
    await shoot(dm, lang, 'book-player');
    await dm.getByTestId('book-draft-more').click();
    await settle(dm, 300);

    // The leak check: a hidden character's name typed into the party's Cast, then Download. Nothing is printed while hits are listed.
    await type(`\n\n${d.npcs[2].name}`);
    await dm.getByTestId('book-draft-download').click();
    await dm.getByTestId('book-draft-leaks').waitFor({ timeout: 30_000 }).catch(async (e) => { await dm.screenshot({ path: join(OUT, 'debug-leak.png') }); throw e; });
    await settle(dm, 800);
    await shoot(dm, lang, 'book-leak');

    // A stale chapter: the GM edited the Pages chapter, then the campaign gained a page.
    const pagesChapter = sections.find((x) => /pages/i.test(x));
    if (pagesChapter) {
      await chapter(pagesChapter).click();
      await settle(dm, 1500);
      await type(`\n\n${d.bookNote}`);
      await dm.getByTestId('book-draft-close').click();
      await settle(dm, 800);
      const p = d.newPage;
      await dm.goto(`/campaigns/${campaignId}/wiki/new?title=${encodeURIComponent(p.title)}`);
      await interactive(dm);
      await dm.getByTestId('wiki-kind-select').selectOption(p.kind);
      await dm.getByTestId('wiki-body').fill(p.body);
      await dm.getByTestId('wiki-body').press('Tab');
      await settle(dm, 300);
      await dm.getByTestId('wiki-visibility').selectOption('Party');
      await dm.getByTestId('wiki-save').click();
      await dm.getByTestId('wiki-page').waitFor();
      await settle(dm, 600);
      await openBook(900);
      await dm.getByTestId('book-tab-draft').click();
      await dm.getByTestId('book-draft-open').click();
      await dialog.waitFor();
      await settle(dm, 2500);
      await chapter(pagesChapter).click();
      await dm.getByTestId('book-draft-stale').waitFor({ timeout: 30_000 });
      await settle(dm, 1500);
      await shoot(dm, lang, 'book-stale');
    }
    await dm.setViewportSize({ width: 1100, height: 820 });
  }

  if (!BOOK_ONLY) {
  // ---- what a player sees, on a phone ----
  const phone = await browser.newContext({
    baseURL: BASE, viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, locale: lang, colorScheme: 'dark',
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

  }

  for (const { ctx } of playerPages) await ctx.close();
  await dmCtx.close();
}

const browser = await chromium.launch(args.chrome ? { executablePath: args.chrome } : {});
try {
  for (const lang of LANGS) await captureLanguage(browser, lang);
} finally {
  await browser.close();
}
