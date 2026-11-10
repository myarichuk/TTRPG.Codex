// Homebrewery's own tests of its Markdown variables (homebrewery-tests/variables.test.js, unchanged) run on the shipped renderer, so a brew's
// "$[...]" renders here as it does in Homebrewery: through the bundle's page splitting, its second render pass and its maths (src/safe-math.js).
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import vm from 'node:vm';

vm.runInThisContext(readFileSync(new URL('../../src/Codex.Web/wwwroot/lib/book/book-render.js', import.meta.url), 'utf8'));
const { pagesHtml } = globalThis.CodexBook;

// A page's markup without the page around it and Homebrewery's filler (an empty last column), as marked-hbfm alone would give it.
const inner = html => html.replace(/^<div class="page[^"]*"[^>]*><div class="columnWrapper">/, '').replace(/<\/div><\/div>$/, '')
  .replace(/<p>&nbsp;<\/p>\s*<div class=['"]columnSplit['"]>\s*<\/div>\s*<p>&nbsp;<\/p>\s*$/, '');
const render = pages => pagesHtml(pages.join('\n\\page\n')).split(/(?=<div class="page)/).map(inner);
globalThis.HomebreweryTests = { hbfm: { render: source => render([source])[0] }, renderAllPages: render };

const results = [];
let group = '';
globalThis.describe = (name, body) => { group = name; body(); };
globalThis.it = (name, body) => {
  try { body.call({}); results.push(null); } catch (e) { results.push(`${group} > ${name}\n  ${e.message}`); }
};
globalThis.expect = actual => ({ toBe: expected => { if (actual !== expected) throw new Error(`got  ${actual}\n  want ${expected}`); } });

const source = readFileSync(new URL('homebrewery-tests/variables.test.js', import.meta.url), 'utf8')
  .replace("import dedent from 'dedent';", `import dedent from ${JSON.stringify(new URL('homebrewery-tests/dedent.mjs', import.meta.url).href)};`)
  .replace("import { hbfm } from 'marked-hbfm';", 'const { hbfm } = globalThis.HomebreweryTests;')
  .replace(/const renderAllPages = function\(pages\)\{[\s\S]*?\n\};/, 'const { renderAllPages } = globalThis.HomebreweryTests;');
if (!source.includes('globalThis.HomebreweryTests.hbfm') && !source.includes('const { hbfm } = globalThis.HomebreweryTests')) throw new Error('variables.test.js: its imports moved');
if (!source.includes('const { renderAllPages }')) throw new Error('variables.test.js: renderAllPages moved');
const dir = mkdtempSync(join(tmpdir(), 'hb-tests-'));
try {
  const file = join(dir, 'variables.test.mjs');
  writeFileSync(file, source);
  await import(pathToFileURL(file).href);
} finally {
  rmSync(dir, { recursive: true, force: true });
}

const failed = results.filter(Boolean);
if (results.length < 50) throw new Error(`only ${results.length} of Homebrewery's variable tests ran`);
if (failed.length) {
  console.error(failed.join('\n'));
  throw new Error(`${failed.length} of ${results.length} of Homebrewery's variable tests failed`);
}
console.log(`Homebrewery's variable tests: all ${results.length} passed`);
