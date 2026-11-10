// Checks of the renderer's bundle against hostile drafts, run with `npm test` after `npm run build` (jsdom stands in for a browser; it has no
// layout, so fitting is tested in the C# suite, which drives a real one).
import { JSDOM } from 'jsdom';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// The shipped bundle, not the source: it is what the editor and the print browser run (and the bundle has no expr-eval).
vm.runInThisContext(readFileSync(new URL('../../src/Codex.Web/wwwroot/lib/book/book-render.js', import.meta.url), 'utf8'));
const { webAddress, pagesHtml, clean, pictures, brew, brewMap, numberPages, footPages, number, pageNumbers, draw, unfit, insertBreaks, isFitted, FIT_CLASS } = globalThis.CodexBook;

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

// A picture the brew names by its https address (docs/BOOK_EXPORT.md 41.25) shows the copy the server keeps, looked up by its canonical address
// in the same map as the slots. The page never fetches: the picture's src is the copy or nothing, never the address. With no copy it is the
// marked box, in the picture's own place (Homebrewery positions its art), so a picture that never comes does not push the text about.
const hosted = 'https://i.imgur.example/Abc123.png?x=1&y=2';
const png = 'data:image/png;base64,iVBORw0KGgo=';
assert.equal(webAddress(hosted + '#frag'), hosted, 'the fragment is not part of the address');
assert.equal(webAddress('HTTPS://I.Imgur.Example'), 'https://i.imgur.example/', 'the host is lower case and the path is there');
for (const no of ['http://i.imgur.example/a.png', 'https://u:p@i.imgur.example/a.png', '//i.imgur.example/a.png', 'data:image/png;base64,AAAA', 'art:' + key,
  '/assets/a.png', 'javascript:alert(1)', '', 'https://', 'https://' + 'a'.repeat(2100)]) {
  assert.equal(webAddress(no), null, no);
}

const copied = drawn(`![Hero](${hosted}){position:absolute,top:10px}`, { [webAddress(hosted)]: png });
assert.equal(copied.querySelector('img').getAttribute('src'), png, copied.innerHTML);
assert.ok(!copied.innerHTML.includes('imgur.example/Abc') || copied.querySelector('img').hasAttribute('data-web'), 'the address is only ever the lookup');
const uncopied = drawn(`![Hero](${hosted}){position:absolute,top:10px}`);
assert.ok(!uncopied.querySelector('img'), uncopied.innerHTML);
const placeholder = uncopied.querySelector('.art-missing');
assert.ok(placeholder && /position:\s*absolute/.test(placeholder.getAttribute('style') || ''), 'it keeps its place: ' + uncopied.innerHTML);
assert.ok(!uncopied.innerHTML.includes('imgur'), 'and the address is nowhere in it: ' + uncopied.innerHTML);
assert.equal(drawn(`<img src="${hosted}" alt="Hero">`, { [webAddress(hosted)]: png }).querySelector('img')?.getAttribute('src'), png, 'the HTML form is a picture too');

// marked-hbfm writes `--HB_src:url(<address>)` into every picture's style, which the strict style rule would take for a load and drop the whole
// style by, and with it the placement the brew gave (Homebrewery's `{position:absolute,top:295px}`). That one declaration goes and the rest
// stays; anything else in a style that could load is still dropped whole, and a slot picture is placed the same way.
const placed = drawn(`![Hero](${hosted}) {position:absolute,top:10px}`, { [webAddress(hosted)]: png }).querySelector('img').getAttribute('style');
assert.ok(/position:\s*absolute/.test(placed) && /top:\s*10px/.test(placed) && !/HB_src|url\(|imgur/.test(placed), placed);
assert.equal(drawn(`![Hero](${hosted}) {background-image:url(https://evil.example/CANARY_BG),top:1px}`, { [webAddress(hosted)]: png }).querySelector('img').getAttribute('style'), null);
// One typed by hand goes with it: the declaration is never kept, whoever wrote it.
const byHand = drawn(`![Hero](${hosted}) {top:1px,--HB_src:url(https://evil.example/CANARY_X)}`, { [webAddress(hosted)]: png }).querySelector('img').getAttribute('style');
assert.ok(byHand === null || (/top:\s*1px/.test(byHand) && !/evil|CANARY|HB_src|url\(/.test(byHand)), byHand);
assert.ok(/position:\s*absolute/.test(drawn(`![s](art:${key}) {position:absolute,top:5px}`, { [key]: png }).querySelector('img').getAttribute('style')), 'a slot picture is placed too');
assert.equal(drawn(`![s](${hosted})`, { [webAddress(hosted)]: png }).querySelector('img').hasAttribute('style'), false, 'no style at all when it had none of its own');

// A copy that is not a plain data image is never written, and an address the map does not hold shows nothing of another's.
assert.ok(!drawn(`![x](${hosted})`, { [webAddress(hosted)]: 'https://evil.example/a.png' }).querySelector('img'));
assert.ok(!drawn(`![x](${hosted})`, { [webAddress(hosted)]: 'data:image/svg+xml;base64,PHN2Zz4=' }).querySelector('img'), 'no SVG');
assert.ok(!drawn(`![x](${hosted})`, { 'https://elsewhere.example/a.png': png }).querySelector('img'));
assert.ok(drawn(`![x](${hosted})`, { [webAddress(hosted)]: 'data:image/avif;base64,AAAA' }).querySelector('img'), 'AVIF, which the server keeps, is shown');
// An attribute typed by hand is not believed: the lookup address comes only from the picture's own src.
assert.ok(!drawn(`<img data-web="${hosted}">`, { [webAddress(hosted)]: png }).querySelector('img'));

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

// A contents the brew wrote in Homebrewery's own shape (`{{ Title}}{{ 9}}`, the number its author guessed) keeps that shape: the number is
// put right in its own span, and no second words span or number is added, or the page's number would print twice.
const authored = drawn('{{toc,wide\n# Contents\n\n- ### [{{ Lore}}{{ 9}}](#ch-lore)\n}}\n\n\\page\n\n{{chapter,#ch-lore\n# Lore\n}}');
number(authored);
const own = authored.querySelector('.toc a');
assert.deepEqual([...own.children].map(c => c.className + ':' + c.textContent.trim()), ['inline-block:Lore', 'inline-block:2'], own.innerHTML);
assert.equal(own.querySelectorAll('.toc-words, .toc-page').length, 0);
number(authored);
assert.equal(authored.querySelector('.toc a').textContent.replace(/\s+/g, ''), 'Lore2', 'numbering again changes nothing');

// The running footer: each page the name of the chapter it is in, from the drawn page's own h1; a chapter that starts mid-page names that
// page; none on the cover or the contents, none where the text wrote its own footnote; beside the columns, and never markup.
const footed = drawn('{{frontCover}}\n\n# Book\n\n\\page\n\n{{toc,wide\n# Contents\n}}\n\n\\page\n\n{{chapter,#ch-lore\n# Lore <i>&amp;</i> Legend\n}}\n\ntext\n\n\\page\n\nmore\n\n{{chapter,#ch-cast\n# Cast\n}}\n\n\\page\n\nstill cast\n\n\\page\n\n{{footnote MINE}}\n\nown');
footPages(footed);
const feet = [...footed.querySelectorAll('.page')].map(p => [...p.querySelectorAll('.footnote')].map(f => f.textContent));
assert.deepEqual(feet, [[], [], ['Lore & Legend'], ['Cast'], ['Cast'], ['MINE']], JSON.stringify(feet));
const foot = footed.querySelectorAll('.page')[3].lastElementChild;
assert.ok(foot.matches('.footnote.auto') && foot.children.length === 0, foot.outerHTML);
footPages(footed);
assert.equal(footed.querySelectorAll('.footnote.auto').length, 3, 'footing again adds nothing');

// Fit pages (41.15). Marking a drawing for it changes no HTML: the same brews, drawn with and without the marks, are the same once the marks are
// taken off, so a rule such as "h1 + p" sees the page as it prints. And each mark points at the line its block starts on.
const unmark = html => html.replace(/ data-fm="[^"]*"/g, '');
const fitBrews = [
  hostile,
  '{{frontCover}}\n\n# Book\n\n\\page\n\n{{chapter,#ch-lore\n# Lore\n}}\n\n## The Tide\nNo blank line after the heading.\n\n- one\n- two\n\n  still two\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n{{note\n##### Note\nInside a block.\n}}\n\n\\column\n\nAfter the column.\n\n```\ncode\n```\n\n> quoted\n\n<div class="wide">\n\nraw div\n\n</div>\n\nTerm\n:: Definition\n',
  '[gold]: 12\n\nThe hoard holds $[gold] coins.\n\n\\page {reliquary}\n\nSecond page, $[gold * 2] coins.\n',
];
for (const md of fitBrews) {
  const marks = { nonce: 'ftest', at: [] };
  const marked = pagesHtml(md, marks);
  assert.equal(unmark(marked), pagesHtml(md), 'marks change nothing else');
  const box = doc.createElement('div');
  box.append(doc.importNode(clean(doc, marked, 'ftest'), true));
  const tagged = [...box.querySelectorAll('[data-fm]')];
  assert.ok(tagged.length > 3, 'blocks are tagged: ' + tagged.length);
  for (const el of tagged) {
    const at = marks.at[Number(el.dataset.fm.split('-')[1])];
    if (at < 0) continue;
    assert.ok(at === 0 || md[at - 1] === '\n', 'a mark is the start of a line: ' + at);
    const text = md.slice(at, md.indexOf('\n', at) < 0 ? undefined : md.indexOf('\n', at));
    assert.ok(text.trim().length > 0, 'a mark is a line with words: ' + JSON.stringify(text));
  }
  // The heading of the second brew is found where it is, though no blank line follows it.
  if (md.includes('## The Tide')) {
    const h = [...box.querySelectorAll('h2')].find(e => e.textContent.includes('The Tide'));
    assert.equal(marks.at[Number(h.dataset.fm.split('-')[1])], md.indexOf('## The Tide'));
    const col = [...box.querySelectorAll('.columnSplit[data-fm]')].find(c => c.nextElementSibling?.textContent.includes('After the column'));
    assert.equal(marks.at[Number(col.dataset.fm.split('-')[1])], md.indexOf('\\column'));
  }
}
// A mark typed into the draft, or one of another drawing, is dropped like any other attribute; so is every mark when nothing is being fitted.
const typed = doc.createElement('div');
typed.append(doc.importNode(clean(doc, pagesHtml('<p data-fm="ftest-0">typed</p>\n\n<p data-fm="other2-1">other</p>\n\n<p data-fm="ftest2-x">bad</p>'), 'ftest2'), true));
assert.equal(typed.querySelectorAll('[data-fm]').length, 0, typed.innerHTML);
assert.equal(drawn('# A\n\ntext').querySelectorAll('[data-fm]').length, 0);

// The chapters' places in the brew, and each drawn page's chapter.
const chs = [{ section: 'front', markdown: 'Cover  \n\n' }, { section: 'lore', markdown: '# Lore\n\nOne\n\n\\page\n\nTwo' }];
const map = brewMap(chs);
const whole = brew(chs);
assert.equal(whole.slice(map[1].start, map[1].start + map[1].length), chs[1].markdown);
const root = doc.getElementById('brew');
draw(doc, root, chs, {});
assert.deepEqual([...root.querySelectorAll('.page')].map(p => p.dataset.section), ['front', 'lore', 'lore']);

// The breaks Fit pages writes: "\page {fitted}" (with the continued page's classes), taken out again exactly, and only those: the GM's own stay.
assert.ok(isFitted('\\page {fitted}') && isFitted('\\page {reliquary,fitted}') && isFitted('  \\page {fitted}  '));
assert.ok(!isFitted('\\page') && !isFitted('\\page {reliquary}') && !isFitted('\\page {fittedx}') && !isFitted('text \\page {fitted}'));
for (const [text, at] of [['A\n\nB', 3], ['A\nB', 2], ['A\n\n\nB', 4], ['# A\n\n\\page\n\nB\n\nC', 15]]) {
  const fitted = insertBreaks(text, [{ at }]);
  assert.equal(unfit(fitted), text, JSON.stringify(fitted));
  assert.equal(insertBreaks(unfit(fitted), [{ at }]), fitted, 'fitting again writes the same text');
}
assert.equal(insertBreaks('A\n\nB', [{ at: 3, classes: ['reliquary'] }]), 'A\n\n\\page {reliquary,fitted}\n\nB');
assert.equal(unfit('A\n\n\\page\n\nB\n\n\\page {fitted}\n\nC'), 'A\n\n\\page\n\nB\n\nC');
// A fitted break draws a page like any other, and the page does not get the class.
const fp = drawn('A\n\n\\page {reliquary,fitted}\n\nB');
assert.equal(fp.querySelectorAll('.page')[1].className, 'page reliquary');
assert.equal(FIT_CLASS, 'fitted');

console.log('book renderer: all checks passed');
