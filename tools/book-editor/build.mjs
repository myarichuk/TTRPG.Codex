// Builds the book's two browser bundles into src/Codex.Web/wwwroot/lib/book (committed; run `npm ci && npm run build` here to change them):
//   book-render.js  the renderer alone, as a script that sets window.CodexBook (the print browser evaluates it in a page with no network);
//   book-editor.js  the editor and the renderer, as an ES module the dialog imports.
// Licences of everything bundled are collected into THIRD-PARTY-NOTICES.txt beside them.
// It also writes the themes' shared layout, Homebrewery's Blank theme (themes/blank.less, compiled to src/Codex.Core/Book/Themes/blank.css,
// embedded in Codex.Core), the Homebrewery compatibility layer (themes/homebrewery-5e.less, layout only, to homebrewery-5e.css beside it),
// and copies the Ashen Vellum theme's fonts from their Fontsource packages into wwwroot/fonts.
import { build } from 'esbuild';
import less from 'less';
import * as csstree from 'css-tree';
import { readFileSync, writeFileSync, mkdirSync, existsSync, copyFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, '..', '..', 'src', 'Codex.Web', 'wwwroot', 'lib', 'book');
mkdirSync(out, { recursive: true });

// expr-eval is never bundled (see src/safe-math.js); the build fails if it gets in by another way.
const common = { alias: { 'expr-eval': join(here, 'src', 'safe-math.js') }, bundle: true, minify: true, metafile: true, legalComments: 'none', target: ['es2020'], mainFields: ['module', 'browser', 'main'], logLevel: 'warning' };
const render = await build({ ...common, entryPoints: [join(here, 'src', 'render.js')], format: 'iife', globalName: 'CodexBook', outfile: join(out, 'book-render.js') });
const editor = await build({ ...common, entryPoints: [join(here, 'src', 'editor.js')], format: 'esm', outfile: join(out, 'book-editor.js') });

for (const meta of [render.metafile, editor.metafile]) {
  if (Object.keys(meta.inputs).some(i => i.includes('node_modules/expr-eval/'))) throw new Error('expr-eval must not be bundled');
}

// Every package whose code is in a bundle, with its licence text.
const packages = new Map();
for (const meta of [render.metafile, editor.metafile]) {
  for (const input of Object.keys(meta.inputs)) {
    const m = /node_modules\/((?:@[^/]+\/)?[^/]+)\//.exec(input);
    if (m) packages.set(m[1], true);
  }
}

// Blank's layout: its LESS compiled as it is, with the provenance header kept as a CSS comment.
const blankLess = readFileSync(join(here, 'themes', 'blank.less'), 'utf8');
const blank = await less.render(blankLess, { filename: join(here, 'themes', 'blank.less') });
if (/url\s*\(|@import|@font-face/i.test(blank.css)) throw new Error('blank.css must load nothing');
const lines = blankLess.split('\n');
const header = lines.slice(0, lines.findIndex(l => !l.startsWith('//'))).map(l => ' *' + l.slice(2)).join('\n');
const themes = join(here, '..', '..', 'src', 'Codex.Core', 'Book', 'Themes');
mkdirSync(themes, { recursive: true });
writeFileSync(join(themes, 'blank.css'), `/*\n${header}\n */\n${blank.css}`);

// The Homebrewery compatibility layer: Homebrewery's 5e PHB theme (themes/homebrewery-5e.less, unchanged) with its look taken out and its
// layout kept, so CSS written for a Homebrewery brew lands where it does there (docs/BOOK_EXPORT.md 41.12). Taken out, by rule, not by hand:
//   - every picture (its asset variables compile to "none"), and what draws one: background-image, border-image, masks;
//   - every colour, turned to the grey of the same lightness (so --HB_Color_* keep their names and contrast, and lose their hues);
//   - what Chrome writes into a PDF as a picture or a second copy of the text: gradients, text-shadow, text stroke, drop-shadow filters,
//     box-shadow; and the drop cap's gradient fill (its letter gets the heading colour instead of a transparent one);
//   - its @font-face rules: the faces it names are bundled (wwwroot/fonts/homebrewery-5e) and added by the server.
const phbLess = readFileSync(join(here, 'themes', 'homebrewery-5e.less'), 'utf8');
// The assets file is not vendored: each picture variable it defines (Homebrewery's /assets URLs) is "none". Its names are found by compiling.
const pictures = [];
let phb;
for (;;) {
  const stub = pictures.map(n => `@${n} : none;`).join('\n');
  const source = phbLess.replace("@import (less) './themes/assets/assets.less';", stub);
  if (source === phbLess) throw new Error('homebrewery-5e.less: the assets import moved');
  try {
    phb = await less.render(source, { filename: join(here, 'themes', 'homebrewery-5e.less') });
    break;
  } catch (e) {
    const m = /variable @(\w+) is undefined/.exec(e.message || '');
    if (!m || pictures.length > 60) throw e;
    pictures.push(m[1]);
  }
}
const layer = layoutOnly(phb.css);
const banned = /url\s*\(|@import|@font-face|gradient\s*\(|text-shadow|text-stroke|drop-shadow|box-shadow|border-image|mask/i;
if (banned.test(layer)) throw new Error('homebrewery-5e.css must load nothing and draw no picture: ' + banned.exec(layer)[0]);
writeFileSync(join(themes, 'homebrewery-5e.css'),
  `/*\n * Homebrewery's "5e PHB" theme (MIT, Copyright (c) 2016 Scott Tolksdorf; https://github.com/naturalcrit/homebrewery, commit 3e600a0),\n` +
  ` * layout only: no pictures, no colours (each is the grey of its lightness), no shadows or gradients. Built by tools/book-editor/build.mjs.\n */\n${layer}`);

// For the parity test only (tests/Codex.Tests/Fixtures, never shipped): the same theme compiled whole, its look and all, pictures aside (they
// load nowhere, and a picture moves nothing on a page). The test lays the same brew out under it and under the layer, word for word.
writeFileSync(join(here, '..', '..', 'tests', 'Codex.Tests', 'Fixtures', 'homebrewery-5e-reference.css'),
  `/* Test fixture: Homebrewery's "5e PHB" theme (MIT, commit 3e600a0) compiled whole, with no pictures. Never shipped. */\n${phb.css}`);

// And Homebrewery's 5e DMG theme, which sits on the PHB one, compiled the same way: the test pastes it as a brew's CSS over the layer, and over
// the PHB theme compiled whole, and the two must still match.
const dmgLess = readFileSync(join(here, 'themes', 'homebrewery-5e-dmg.less'), 'utf8');
const dmgSource = dmgLess.replace("@import (less) './themes/assets/assets.less';", pictures.map(n => `@${n} : none;`).join('\n') + '\n@partCoverHeaderDMG : none;');
if (dmgSource === dmgLess) throw new Error('homebrewery-5e-dmg.less: the assets import moved');
const dmg = await less.render(dmgSource, { filename: join(here, 'themes', 'homebrewery-5e-dmg.less') });
writeFileSync(join(here, '..', '..', 'tests', 'Codex.Tests', 'Fixtures', 'homebrewery-5e-dmg.css'),
  `/* Test fixture: Homebrewery's "5e DMG" theme (MIT, commit 3e600a0) compiled whole, with no pictures. Never shipped. */\n${dmg.css}`);

function layoutOnly(css) {
  const ast = csstree.parse(css, { parseValue: true, parseCustomProperty: false });
  const dropped = /^(background-image|border-image(-.*)?|-webkit-border-image|mask(-.*)?|-webkit-mask(-.*)?|text-shadow|-webkit-text-stroke(-.*)?|paint-order|box-shadow|filter|-webkit-filter|-webkit-background-clip|background-clip)$/i;
  csstree.walk(ast, {
    visit: 'Atrule',
    enter(node, item, list) {
      if (/^(font-face|import)$/i.test(node.name)) list.remove(item);
    }
  });
  csstree.walk(ast, {
    visit: 'Rule',
    enter(rule) {
      const decls = rule.block.children;
      // The drop cap: a transparent letter painted by a gradient. Without the gradient it takes the heading colour.
      let clipped = false;
      decls.forEach(d => { if (d.type === 'Declaration' && /background-clip/i.test(d.property) && csstree.generate(d.value).trim() === 'text') clipped = true; });
      let framed = false;
      let coloured = false;
      decls.forEach((d, item) => {
        if (d.type !== 'Declaration') return;
        const value = d.value.type === 'Raw' ? d.value.value : csstree.generate(d.value);
        if (/^border(-(top|right|bottom|left))?(-color)?$/i.test(d.property) && d.property !== 'border-width') coloured = true;
        if (dropped.test(d.property) || /url\s*\(|gradient\s*\(/i.test(value)) {
          if (/border-image/i.test(d.property)) framed = true;
          decls.remove(item);
          return;
        }
        if (clipped && d.property === 'color') {
          d.value = csstree.parse('var(--HB_Color_HeaderText)', { context: 'value' });
          return;
        }
        if (d.value.type === 'Raw') d.value.value = greyText(d.value.value);
        else d.value = csstree.parse(greyText(csstree.generate(d.value)), { context: 'value' });
      });
      // A frame that was a picture keeps its width (it is layout) but draws nothing: a theme gives it a colour.
      if (framed && !coloured) decls.appendData(csstree.parse('border-color:transparent', { context: 'declaration' }));
    }
  });
  return csstree.generate(ast).replace(/\}/g, '}\n');
}

// Every colour written in a value, as the grey of its lightness (Rec. 709 luma), alpha kept.
function greyText(value) {
  const named = { black: '#000000', white: '#FFFFFF' };
  value = value.replace(/#([0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{3,4})\b/gi, (_, h) => {
    if (h.length <= 4) h = [...h].map(c => c + c).join('');
    const [r, g, b] = [0, 2, 4].map(i => parseInt(h.slice(i, i + 2), 16));
    const y = Math.round(0.2126 * r + 0.7152 * g + 0.0722 * b).toString(16).padStart(2, '0');
    return '#' + y + y + y + (h.length === 8 ? h.slice(6) : '');
  });
  value = value.replace(/rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)\s*(?:[,/]\s*([\d.]+%?))?\s*\)/gi, (_, r, g, b, a) => {
    const y = Math.round(0.2126 * r + 0.7152 * g + 0.0722 * b);
    return a === undefined ? `rgb(${y},${y},${y})` : `rgba(${y},${y},${y},${a})`;
  });
  if (/\b(hsla?|hwb|lab|lch|oklab|oklch|color)\(/i.test(value)) throw new Error('homebrewery-5e.less: a colour form the layer does not turn grey: ' + value);
  return value;
}

// Ashen Vellum's faces: the Latin files of each package, unchanged, with the package's licence beside them.
const fonts = join(here, '..', '..', 'src', 'Codex.Web', 'wwwroot', 'fonts');
for (const [pkg, files] of Object.entries({
  'im-fell-english': ['latin-400-normal', 'latin-400-italic'],
  'im-fell-english-sc': ['latin-400-normal'],
  'im-fell-dw-pica': ['latin-400-normal', 'latin-400-italic'],
  'im-fell-dw-pica-sc': ['latin-400-normal'],
  'pirata-one': ['latin-400-normal'],
  'special-elite': ['latin-400-normal'],
})) {
  const from = join(here, 'node_modules', '@fontsource', pkg);
  mkdirSync(join(fonts, pkg), { recursive: true });
  for (const f of files) copyFileSync(join(from, 'files', `${pkg}-${f}.woff2`), join(fonts, pkg, `${pkg}-${f}.woff2`));
  const licence = readFileSync(join(from, 'LICENSE'), 'utf8');
  copyFileSync(join(from, 'LICENSE'), join(fonts, pkg, /Apache License/.test(licence) ? 'LICENSE.txt' : 'OFL.txt'));
}

let notices = 'Third-party code bundled in book-render.js and book-editor.js, with each package\'s licence.\n';
for (const name of [...packages.keys()].sort()) {
  const root = join(here, 'node_modules', name);
  const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));
  const file = ['LICENSE', 'LICENSE.md', 'LICENSE.txt', 'license', 'LICENCE', 'license.md'].map(f => join(root, f)).find(existsSync);
  notices += `\n================================================================\n${name} ${pkg.version} (${pkg.license})\n${pkg.homepage || ''}\n\n`;
  notices += file ? readFileSync(file, 'utf8').trim() + '\n' : `No licence file is shipped in the package; its package.json declares ${pkg.license}.\n`;
}
// Not a package: the Blank layout every book theme sits on (themes/blank.less, compiled into Codex.Core).
notices += `\n================================================================\nHomebrewery "Blank" theme layout (themes/V3/Blank/style.less, commit 3e600a0, trimmed) and "5e PHB" theme layout (themes/V3/5ePHB/style.less, the same commit, layout only, the Homebrewery compatibility layer) (MIT)\nhttps://github.com/naturalcrit/homebrewery\n\n${readFileSync(join(here, 'themes', 'HOMEBREWERY-LICENSE.txt'), 'utf8').trim()}\n`;
writeFileSync(join(out, 'THIRD-PARTY-NOTICES.txt'), notices);
console.log(`book-render.js, book-editor.js and notices for ${packages.size} packages written to ${out}`);
