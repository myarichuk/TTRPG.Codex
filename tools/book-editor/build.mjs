// Builds the book's two browser bundles into src/Codex.Web/wwwroot/lib/book (committed; run `npm ci && npm run build` here to change them):
//   book-render.js  the renderer alone, as a script that sets window.CodexBook (the print browser evaluates it in a page with no network);
//   book-editor.js  the editor and the renderer, as an ES module the dialog imports.
// Licences of everything bundled are collected into THIRD-PARTY-NOTICES.txt beside them.
import { build } from 'esbuild';
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, '..', '..', 'src', 'Codex.Web', 'wwwroot', 'lib', 'book');
mkdirSync(out, { recursive: true });

const common = { bundle: true, minify: true, metafile: true, legalComments: 'none', target: ['es2020'], mainFields: ['module', 'browser', 'main'], logLevel: 'warning' };
const render = await build({ ...common, entryPoints: [join(here, 'src', 'render.js')], format: 'iife', globalName: 'CodexBook', outfile: join(out, 'book-render.js') });
const editor = await build({ ...common, entryPoints: [join(here, 'src', 'editor.js')], format: 'esm', outfile: join(out, 'book-editor.js') });

// Every package whose code is in a bundle, with its licence text.
const packages = new Map();
for (const meta of [render.metafile, editor.metafile]) {
  for (const input of Object.keys(meta.inputs)) {
    const m = /node_modules\/((?:@[^/]+\/)?[^/]+)\//.exec(input);
    if (m) packages.set(m[1], true);
  }
}

let notices = 'Third-party code bundled in book-render.js and book-editor.js, with each package\'s licence.\n';
for (const name of [...packages.keys()].sort()) {
  const root = join(here, 'node_modules', name);
  const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));
  const file = ['LICENSE', 'LICENSE.md', 'LICENSE.txt', 'license', 'LICENCE', 'license.md'].map(f => join(root, f)).find(existsSync);
  notices += `\n================================================================\n${name} ${pkg.version} (${pkg.license})\n${pkg.homepage || ''}\n\n`;
  notices += file ? readFileSync(file, 'utf8').trim() + '\n' : `No licence file is shipped in the package; its package.json declares ${pkg.license}.\n`;
}
writeFileSync(join(out, 'THIRD-PARTY-NOTICES.txt'), notices);
console.log(`book-render.js, book-editor.js and notices for ${packages.size} packages written to ${out}`);
