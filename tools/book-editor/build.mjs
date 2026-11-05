// Builds the book's two browser bundles into src/Codex.Web/wwwroot/lib/book (committed; run `npm ci && npm run build` here to change them):
//   book-render.js  the renderer alone, as a script that sets window.CodexBook (the print browser evaluates it in a page with no network);
//   book-editor.js  the editor and the renderer, as an ES module the dialog imports.
// Licences of everything bundled are collected into THIRD-PARTY-NOTICES.txt beside them.
// It also writes the themes' shared layout, Homebrewery's Blank theme (themes/blank.less, compiled to src/Codex.Core/Book/Themes/blank.css,
// embedded in Codex.Core), and copies the Ashen Vellum theme's fonts from their Fontsource packages into wwwroot/fonts.
import { build } from 'esbuild';
import less from 'less';
import { readFileSync, writeFileSync, mkdirSync, existsSync, copyFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, '..', '..', 'src', 'Codex.Web', 'wwwroot', 'lib', 'book');
mkdirSync(out, { recursive: true });

// expr-eval is never bundled (see src/no-math.js); the build fails if it gets in by another way.
const common = { alias: { 'expr-eval': join(here, 'src', 'no-math.js') }, bundle: true, minify: true, metafile: true, legalComments: 'none', target: ['es2020'], mainFields: ['module', 'browser', 'main'], logLevel: 'warning' };
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

// Ashen Vellum's faces: the Latin files of each package, unchanged, with the package's licence beside them.
const fonts = join(here, '..', '..', 'src', 'Codex.Web', 'wwwroot', 'fonts');
for (const [pkg, files] of Object.entries({
  'im-fell-english': ['latin-400-normal', 'latin-400-italic'],
  'im-fell-english-sc': ['latin-400-normal'],
  'im-fell-dw-pica': ['latin-400-normal', 'latin-400-italic'],
  'im-fell-dw-pica-sc': ['latin-400-normal'],
  'pirata-one': ['latin-400-normal'],
  'unifrakturmaguntia': ['latin-400-normal'],
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
notices += `\n================================================================\nHomebrewery "Blank" theme layout (themes/V3/Blank/style.less, commit 3e600a0, trimmed) (MIT)\nhttps://github.com/naturalcrit/homebrewery\n\n${readFileSync(join(here, 'themes', 'HOMEBREWERY-LICENSE.txt'), 'utf8').trim()}\n`;
writeFileSync(join(out, 'THIRD-PARTY-NOTICES.txt'), notices);
console.log(`book-render.js, book-editor.js and notices for ${packages.size} packages written to ${out}`);
