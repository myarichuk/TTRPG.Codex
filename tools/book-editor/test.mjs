// Checks of the renderer's bundle against hostile drafts, run with `npm test` after `npm run build` (jsdom stands in for a browser; it has no
// layout, so fitting is tested in the C# suite, which drives a real one).
import { JSDOM } from 'jsdom';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// The shipped bundle, not the source: it is what the editor and the print browser run (and the bundle has no expr-eval).
vm.runInThisContext(readFileSync(new URL('../../src/Codex.Web/wwwroot/lib/book/book-render.js', import.meta.url), 'utf8'));
const { pagesHtml, clean, pictures, brew, numberPages, number, pageNumbers } = globalThis.CodexBook;

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

// "$[...]" maths is Homebrewery's (src/safe-math.js, not expr-eval, which is not in the bundle): sums and the listed functions work; a name
// that is not a listed function, a member, a string or anything else is left as written, and nothing runs.
globalThis.PWNED = undefined;
const maths = drawn('[hp]: 9\n\nHit points: $[2+3*hp]; $[floor(hp/2)]; $[toRomans(hp)]; $[signed(-hp)].\n\n'
  + '$[constructor.constructor("globalThis.PWNED=1")()] $[constructor(1)] $[__proto__(1)] $[toString(1)] $[hasOwnProperty(1)] $[valueOf()] '
  + '$[' + '('.repeat(400) + '1' + ')'.repeat(400) + '] $[1e999999]').innerHTML;
assert.equal(globalThis.PWNED, undefined);
assert.ok(maths.includes('Hit points: 29; 4; IX; -9.'), 'maths was done: ' + maths);
assert.ok(maths.includes('proto</strong>(1)]'), '__proto__ was left as written (in bold: it is Markdown)');
for (const name of ['constructor(1)', 'toString(1)', 'hasOwnProperty(1)', 'valueOf()']) assert.ok(maths.includes('$[' + name + ']'), name + ' was left as written');
assert.ok(maths.includes('$[((('), 'too deep is left as written');

// A variable set on a later page is known on an earlier one, as in Homebrewery's editor, though the print browser renders the brew once.
assert.ok(drawn('The $[ship] sails.\n\n\\page\n\n[ship]: Tern').innerHTML.includes('The Tern sails.'));

// A page's own classes and styles, as Homebrewery reads them from its \page line: a plain class goes on the page (the sheet's dark pages
// are "\page {reliquary}"); a style that could load anything is dropped, and a class that is not a plain name is left out, never the page's own.
const styled = drawn('first\n\n\\page {reliquary,soot}\n\ndark\n\n\\page {background:url(https://evil.example/CANARY_PAGE),bad"onclick=x}\n\nthird\n\n\\page {color:#a00}\n\nfourth');
const stp = [...styled.querySelectorAll('.page')];
assert.equal(stp.length, 4);
assert.equal(stp[1].className, 'page reliquary soot');
assert.ok(stp[2].classList.contains('page') && !stp[2].hasAttribute('style'), stp[2].outerHTML.slice(0, 200));
assert.ok(!styled.innerHTML.includes('CANARY_PAGE') && !styled.innerHTML.includes('onclick'), styled.innerHTML);
assert.equal(stp[3].getAttribute('style'), 'color:#a00');

// What the generator writes for a break in campaign text ("\\\\page", escaped) is words on the page, not a break.
const escaped = drawn('Bio\n\n\\\\page {display:none}\n\\\\column\n');
assert.equal(escaped.querySelectorAll('.page').length, 1);
assert.equal(escaped.querySelectorAll('.columnSplit').length, 1, 'only the filler column: ' + escaped.innerHTML);
assert.ok(escaped.textContent.includes('\\page') && escaped.textContent.includes('\\column'), escaped.innerHTML);

// Page numbers as Homebrewery's Blank counts them: every page counts (the cover too) unless it skips, a reset page is 1; the renderer gives
// each page Homebrewery's number element, except covers and pages whose text placed one; contents lines take the counted number, in
// Homebrewery's two-span shape.
const numbered = drawn('{{frontCover}}\n\n# Book\n\n\\page\n\n{{toc,wide\n# Contents\n\n- [Lore](#ch-lore)\n}}\n\n\\page\n\n{{skipCounting}}\n\nmap\n\n\\page\n\n{{chapter,#ch-lore\n# Lore\n}}\n\n\\page\n\n{{pageNumber,auto}}\n\n{{resetCounting}}\n\nafter');
assert.deepEqual(pageNumbers(numbered), [1, 2, 2, 3, 1]);
numberPages(numbered);
const pagesOf = [...numbered.querySelectorAll('.page')];
assert.deepEqual(pagesOf.map(p => p.querySelectorAll('.pageNumber').length), [0, 1, 1, 1, 1], 'one number a page, none on the cover');
assert.ok(pagesOf[1].lastElementChild.matches('.pageNumber.auto'), 'beside the columns, not in them');
number(numbered);
const line = numbered.querySelector('.toc a');
assert.equal(line.innerHTML, '<span class="toc-words">Lore</span><span class="toc-page">3</span>');
number(numbered);
assert.equal(numbered.querySelector('.toc a').innerHTML, line.innerHTML, 'numbering again changes nothing');

console.log('book renderer: all checks passed');
