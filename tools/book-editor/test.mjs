// Checks of the renderer's bundle against hostile drafts, run with `npm test` after `npm run build` (jsdom stands in for a browser; it has no
// layout, so fitting is tested in the C# suite, which drives a real one).
import { JSDOM } from 'jsdom';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// The shipped bundle, not the source: it is what the editor and the print browser run (and the bundle has no expr-eval).
vm.runInThisContext(readFileSync(new URL('../../src/Codex.Web/wwwroot/lib/book/book-render.js', import.meta.url), 'utf8'));
const { pagesHtml, clean, pictures, brew } = globalThis.CodexBook;

const dom = new JSDOM('<!doctype html><div id="brew"></div>');
const doc = dom.window.document;
globalThis.CSS = { escape: s => s };

function drawn(markdown, art = {}) {
  const box = doc.createElement('div');
  box.append(doc.importNode(clean(doc, pagesHtml(markdown)), true));
  pictures(box, art);
  return box;
}

const key = 's0123456789abcdef01234567';
const hostile = String.raw`# Title

<script>window.PWNED=1</script>

<img src=x onerror="window.PWNED=2">

<iframe src="https://evil.example"></iframe>

<svg><script>1</script></svg>

<a href="https://evil.example/CANARY_LINK">outside</a> <a href="javascript:alert(1)">js</a> <a href="#ch-cast">inside</a>

<div style="background:url(https://evil.example/CANARY_STYLE)">styled</div>

<div style="color:red">red</div>

<form action="https://evil.example"><input name=a></form>

<meta http-equiv="refresh" content="0;url=https://evil.example">

<style>body{background:url(https://evil.example/CANARY_SHEET)}</style>

<object data="https://evil.example"></object>

<link rel=stylesheet href="https://evil.example/x.css">

![picture](https://evil.example/CANARY_IMG.png)

![data](data:image/png;base64,AAAA)

![slot](art:${key})

![other](art:${'s' + 'f'.repeat(24)})

<img data-art="${key}">

<p onclick="x()" class="ok" id="fine">para</p>

{{wide,background:url(https://evil.example/CANARY_BLOCK)
block
}}
`;

const html = drawn(hostile, { [key]: 'data:image/png;base64,iVBORw0KGgo=' }).innerHTML;
for (const bad of ['<script', 'onerror', '<iframe', '<svg', 'javascript:', '<form', '<input', '<meta', '<style', '<object', '<link', 'onclick']) {
  assert.ok(!html.includes(bad), `"${bad}" survived: ${html}`);
}
// An address may stay as words a GM typed; it is never an attribute (a link, a picture, a style) that the page would follow.
const box = doc.createElement('div');
box.innerHTML = html;
for (const el of box.querySelectorAll('*')) {
  for (const { name, value } of [...el.attributes]) {
    assert.ok(!/evil\.example|CANARY/.test(value), `${el.localName} ${name}="${value}" survived`);
  }
}
assert.ok(html.includes('href="#ch-cast"'), 'a link inside the book stays');
assert.ok(html.includes('style="color:red"'), 'a harmless style stays');
assert.ok(html.includes('src="data:image/png;base64,iVBORw0KGgo="'), 'a slot the edition has a picture for shows it');
assert.equal((html.match(/<img/g) || []).length, 1, 'only the resolved slot is a picture: ' + html);
assert.ok(html.includes('class="art-missing"'), 'a slot with no picture is a marked box');
assert.ok(html.includes('class="ok" id="fine"'));

// A picture value that is not a plain data image is never written.
const odd = drawn(`![x](art:${key})`, { [key]: 'data:image/png;base64,AAA" onerror="x' }).innerHTML;
assert.ok(!odd.includes('<img'), odd);
const remote = drawn(`![x](art:${key})`, { [key]: 'https://evil.example/a.png' }).innerHTML;
assert.ok(!remote.includes('evil'), remote);

// Pages: one per \page, chapters joined by one.
const book = brew([{ markdown: '# A\n\ntext' }, { markdown: '# B\n\n\\page\n\nmore' }]);
assert.equal(drawn(book).querySelectorAll('.page').length, 3);

// Secret blocks are a styled block, and ":::" is not ours (Homebrewery reads it as a definition list): the generator never writes it.
assert.ok(drawn('{{secret\nThe truth.\n}}').querySelector('.block.secret'));

// "$[...]" maths is switched off (expr-eval is not in the bundle): a hostile expression evaluates nothing and nothing runs.
globalThis.PWNED = undefined;
const maths = drawn('Hit points: $[2+3]. $[constructor.constructor("globalThis.PWNED=1")()] $[x:4] then $[x].').innerHTML;
assert.equal(globalThis.PWNED, undefined);
assert.ok(!maths.includes('>5<') && !/\b5\b/.test(maths), 'no maths was done: ' + maths);
assert.ok(maths.includes('4'), 'a plain variable still works: ' + maths);

console.log('book renderer: all checks passed');
