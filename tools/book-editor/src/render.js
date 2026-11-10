// The campaign book's renderer (docs/BOOK_EXPORT.md 41.6): one script for the editor's preview (in the GM's browser) and for the PDF (in the
// print browser), so the two lay a book out the same way. It turns the draft's Homebrewery-flavoured Markdown into Homebrewery's fixed pages
// with marked-hbfm, cleans the result to a short list of tags and attributes (marked-hbfm passes HTML through, and a draft is typed by hand),
// puts the edition's pictures in, and moves whatever spills past a page onto a page after it, because a page never breaks by itself.
import { hbfm } from 'marked-hbfm';

// A "\page" line, with or without Homebrewery's options, starts a page (as Homebrewery splits a brew before rendering it).
const PAGE_BREAK = /^(?=\\page(?:break)?(?: *{[^\n{}]*})?$)/m;

const KEEP = new Set(['div', 'span', 'p', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'em', 'strong', 'b', 'i', 'u', 's', 'del', 'ins', 'sub', 'sup', 'br', 'hr',
  'ul', 'ol', 'li', 'dl', 'dt', 'dd', 'table', 'thead', 'tbody', 'tfoot', 'tr', 'th', 'td', 'caption', 'colgroup', 'col', 'blockquote', 'pre', 'code',
  'img', 'a', 'figure', 'figcaption', 'small', 'mark', 'abbr', 'cite', 'q', 'kbd', 'var', 'samp', 'wbr', 'bdi', 'bdo', 'center']);
// Gone with everything inside: what runs, loads, submits, or carries markup of its own.
const DROP = new Set(['script', 'style', 'link', 'meta', 'base', 'iframe', 'frame', 'frameset', 'object', 'embed', 'applet', 'param', 'svg', 'math',
  'template', 'noscript', 'form', 'input', 'button', 'textarea', 'select', 'option', 'video', 'audio', 'source', 'track', 'canvas', 'picture',
  'portal', 'dialog', 'slot', 'title', 'head', 'xmp', 'plaintext', 'noembed', 'noframes']);
const ATTRIBUTES = new Set(['class', 'id', 'alt', 'title', 'colspan', 'rowspan', 'start', 'reversed', 'dir', 'lang', 'style', 'href', 'align', 'width', 'height']);
const SLOT = /^art:(s[0-9a-f]{24})$/;
// What a picture is allowed to be once it is in the page: a data URL of a kind the server keeps copies of (BookCssWeb.Sniff).
const PICTURE = /^data:image\/(png|jpeg|webp|gif|avif);base64,[A-Za-z0-9+/=]+$/;

/**
 * The address a markdown picture names, in the form the server keeps its copy under (BookCssWeb.Address, docs/BOOK_EXPORT.md 41.25): https
 * only, no credentials, no fragment. Null for anything else (a relative address, another scheme).
 */
export function webAddress(src) {
  if (typeof src !== 'string' || src.length > 2048) return null;
  try {
    const u = new URL(src.trim());
    if (u.protocol !== 'https:' || u.username || u.password || !u.hostname) return null;
    u.hash = '';
    return u.href;
  } catch {
    return null;
  }
}
// Nothing in a style may load or run: no url(), no image functions, no escapes that could spell one, no legacy script hooks.
const UNSAFE_STYLE = /url\s*\(|image-set|image\s*\(|element\s*\(|cross-fade|expression|behavior|binding|@import|\\|javascript:|src\s*\(/i;
// The one declaration marked-hbfm writes into a picture's style: its own address as a custom property (not a load; nothing here reads it).
const HB_SRC = /--HB_src\s*:\s*url\(\s*[^)\s"'\\]*\s*\)\s*;?/gi;

// The class Fit pages gives the breaks it writes ("\page {fitted}"), so a second run takes them out before fitting again and leaves the GM's own
// breaks where they are. The page itself does not get the class.
export const FIT_CLASS = 'fitted';
const JOIN = '\n\n\\page\n\n';

/** The draft as one brew: its chapters in order, each starting a page. */
export function brew(chapters) {
  return chapters.map(c => c.markdown.replace(/\s+$/, '')).join(JOIN);
}

/** Where each chapter starts in `brew(chapters)`, with its section and length there. */
export function brewMap(chapters) {
  let at = 0;
  return chapters.map(c => {
    const length = c.markdown.replace(/\s+$/, '').length;
    const entry = { section: c.section ?? null, start: at, length };
    at += length + JOIN.length;
    return entry;
  });
}

/** Homebrewery's pages, as marked-hbfm renders them, before cleaning. With `marks` (Fit pages), every top-level block is tagged with where its
 * Markdown starts in `markdown` (see `marking`). */
export function pagesHtml(markdown, marks) {
  const pages = markdown.split(PAGE_BREAK);
  // Twice when the brew has variables: marked-hbfm learns a variable set on a later page only by rendering that page, and Homebrewery's editor,
  // rendering on every keystroke, always has. The print browser renders once, so without the first pass its pages would not match the preview.
  if (markdown.includes('[')) renderPages(pages);
  return renderPages(pages, marks);
}

function renderPages(pages, marks) {
  let at = 0;
  return pages.map((text, i) => {
    const start = at;
    at += text.length;
    let tags = { classes: '', style: '' };
    let body = start;
    if (text.startsWith('\\page')) {
      tags = pageTags(text.split('\n', 1)[0]);
      body = start + (text.includes('\n') ? text.indexOf('\n') + 1 : text.length);
      text = text.includes('\n') ? text.substring(text.indexOf('\n') + 1) : '';
    }
    const source = text;
    // Homebrewery's own filler: an empty last column, so a page's columns fill from the left.
    text += '\n\n&nbsp;\n\\column\n&nbsp;';
    let html;
    try {
      html = marks ? marking(marks, source, body, () => hbfm.render(text, i)) : hbfm.render(text, i);
    } catch (e) {
      html = '<p class="render-failed"></p>';
    }
    const style = tags.style ? ` style="${tags.style.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;')}"` : '';
    return `<div class="page${tags.classes}"${style} id="p${i + 1}"><div class="columnWrapper">${html}</div></div>`;
  }).join('');
}

/**
 * Renders a page with each of its top-level blocks tagged (`data-fm`) with the offset in the brew where the block's Markdown starts, or -1 where
 * it cannot be told (a line the variables changed, Homebrewery's filler). marked-hbfm renders through `marked.parser`; for this one call, the
 * page's top-level tokens go through it one at a time (as the parser takes them anyway, each on its own), so the HTML is the same, and the
 * first element of each gets the tag. Nothing is added to the Markdown or between blocks: a rule such as `h1 + p` sees the page as it prints.
 */
function marking(marks, source, body, render) {
  const marked = hbfm.marked;
  const parser = marked.parser;
  const lines = [];
  for (let at = 0, n = 0; at <= source.length; n++) {
    const end = source.indexOf('\n', at);
    const line = end < 0 ? source.substring(at) : source.substring(at, end);
    lines.push({ at, line: line.replace(/\s+$/, '') });
    if (end < 0) break;
    at = end + 1;
  }
  let cursor = 0;
  const locate = raw => {
    let first = raw.split('\n').find(l => l.trim()) ?? '';
    first = first.replace(/\s+$/, '');
    const column = /^<div class='columnSplit'><\/div>$/.test(first.trim());
    for (let i = cursor; i < lines.length; i++) {
      const line = lines[i].line;
      if (column ? /^\\column(?:break)?$/.test(line.trim()) : line === first) {
        cursor = i + 1;
        return body + lines[i].at;
      }
    }
    return -1;
  };
  let depth = 0;
  marked.parser = function (tokens, options) {
    if (depth++ > 0) {
      try { return parser.call(this, tokens, options); } finally { depth--; }
    }
    try {
      let html = '';
      for (const token of tokens) {
        const one = Object.assign([token], { links: tokens.links });
        let part = parser.call(this, one, options);
        if (token.type !== 'space') {
          const k = marks.at.push(locate(token.raw || '')) - 1;
          part = part.replace(/^(\s*<[A-Za-z][\w-]*)/, `$1 data-fm="${marks.nonce}-${k}"`);
        }
        html += part;
      }
      return html;
    } finally {
      depth--;
    }
  };
  try {
    return render();
  } finally {
    marked.parser = parser;
  }
}

/**
 * A page's own classes and styles, as Homebrewery reads them from its "\page {reliquary,background:#000}" line (the first line's injected
 * tags, through marked-hbfm's own parser). Written into the page's markup here and cleaned with the rest: a class keeps to letters, digits,
 * dashes and spaces, and a style is dropped whole if anything in it could load or run.
 */
function pageTags(line) {
  let injected;
  try {
    injected = hbfm.marked.lexer(line)[0]?.tokens?.find(t => t.injectedTags !== undefined)?.injectedTags;
  } catch {
    injected = null;
  }
  if (!injected) return { classes: '', style: '' };
  // Only plain class names: one that is not would make the cleaner drop the whole class attribute, and the page would stop being a page.
  const classes = String(injected.classes || '').split(/\s+/).filter(c => /^[A-Za-z][\w-]{0,40}$/.test(c) && c !== 'page' && c !== FIT_CLASS).map(c => ' ' + c).join('');
  const style = injected.styles ? Object.entries(injected.styles).map(([k, v]) => `${k}:${v}`).join(';') : '';
  return { classes, style };
}

/** The pages, cleaned, as nodes of `doc`. Parsed inert (a template's content runs nothing and loads nothing), then walked. */
export function clean(doc, html, nonce) {
  const template = doc.createElement('template');
  template.innerHTML = html;
  const walk = node => {
    for (let child = node.firstChild; child;) {
      const next = child.nextSibling;
      if (child.nodeType === 1) {
        const tag = child.localName;
        if (DROP.has(tag) || child.namespaceURI !== 'http://www.w3.org/1999/xhtml') {
          child.remove();
        } else if (!KEEP.has(tag)) {
          walk(child);
          child.replaceWith(...child.childNodes);
        } else {
          attributes(child, nonce);
          if (tag === 'img' && !child.hasAttribute('data-art') && !child.hasAttribute('data-web')) child.remove();
          else walk(child);
        }
      } else if (child.nodeType !== 3) {
        child.remove();
      }
      child = next;
    }
  };
  walk(template.content);
  return template.content;
}

function attributes(el, nonce) {
  const tag = el.localName;
  const src = tag === 'img' ? el.getAttribute('src') : null;
  for (const { name, value: written } of [...el.attributes]) {
    // marked-hbfm gives every picture a custom property holding its address (`--HB_src:url(…)`), which the strict rule below would take for a
    // load and drop the whole style by, and with it everything the brew placed the picture by (`{position:absolute,top:295px}`). That one
    // declaration goes; whatever else is left in the style is held to the same rule as ever.
    const value = tag === 'img' && name === 'style' ? written.replace(HB_SRC, '').trim() : written;
    // A Fit pages tag, only as this drawing wrote it: one typed into the draft is dropped like any other attribute.
    if (name === 'data-fm' && nonce && value.startsWith(nonce + '-') && /^\d+$/.test(value.slice(nonce.length + 1))) continue;
    const keep = ATTRIBUTES.has(name)
      && !(name === 'href' && !/^#[\w-]{1,120}$/.test(value))
      && !(name === 'style' && UNSAFE_STYLE.test(value))
      && !((name === 'width' || name === 'height') && !/^\d{1,4}%?$/.test(value))
      && !(name === 'id' && !/^[\w-]{1,120}$/.test(value))
      && !(name === 'class' && !/^[\w -]{0,300}$/.test(value));
    if (!keep || (name === 'style' && !value)) el.removeAttribute(name);
    else if (value !== written) el.setAttribute(name, value);
  }
  if (tag === 'img') {
    const slot = src && SLOT.exec(src.trim());
    if (slot) {
      el.setAttribute('data-art', slot[1]);
    } else if (webAddress(src)) {
      // Asks for the server's copy by this address; the picture's own src is never set to it, so the page fetches nothing.
      el.setAttribute('data-web', webAddress(src));
    }
  }
  // A link only within the book, and one that goes nowhere stays as its words.
  if (tag === 'a' && !el.hasAttribute('href')) el.setAttribute('data-plain', '');
}

/** The edition's pictures in place: a slot it has a picture for shows it, one it has none for shows a marked empty box (not printed). A picture
 * the brew names by its web address (41.25) is looked up by that address in the same map, where the server put the copy it keeps. */
export function pictures(root, art) {
  for (const img of root.querySelectorAll('img[data-art], img[data-web]')) {
    const key = img.dataset.art ?? img.dataset.web;
    const uri = art && Object.prototype.hasOwnProperty.call(art, key) ? art[key] : null;
    if (typeof uri === 'string' && PICTURE.test(uri)) {
      img.src = uri;
    } else {
      const box = img.ownerDocument.createElement('span');
      box.className = 'art-missing';
      box.textContent = img.alt || '';
      // A web picture the brew placed (Homebrewery's `{position:absolute,…}`) keeps its place while it has no copy, so a picture that never
      // comes does not push the text about; the style was cleaned with the rest.
      if (img.dataset.web) {
        if (img.className) box.className += ' ' + img.className;
        if (img.hasAttribute('style')) box.setAttribute('style', img.getAttribute('style'));
      }

      img.replaceWith(box);
    }
  }
}

/** The book drawn into `root` (a div of `doc`). `input` is the brew, or the chapters (`[{section, markdown}]`): then each page knows its
 * chapter (`data-section`), for the overflow report. With `marks` (Fit pages), blocks carry where their Markdown starts (`marking`). */
export function draw(doc, root, input, art, marks) {
  const chapters = Array.isArray(input) ? input : null;
  const markdown = chapters ? brew(chapters) : String(input ?? '');
  const pages = doc.createElement('div');
  pages.className = 'pages';
  pages.append(doc.importNode(clean(doc, pagesHtml(markdown, marks), marks?.nonce), true));
  pictures(pages, art);
  if (chapters) {
    const map = brewMap(chapters);
    // A page never spans two chapters (they are joined by a break), so its chapter is the last one starting before the page's text ends.
    let at = 0;
    const ends = markdown.split(PAGE_BREAK).map(t => (at += t.length));
    [...pages.querySelectorAll(':scope > .page')].forEach((page, i) => {
      const chapter = map.filter(c => c.start < ends[i]).pop();
      if (chapter?.section) page.dataset.section = chapter.section;
    });
  }
  root.replaceChildren(pages);
  return pages;
}

/** Waits for the pictures and the fonts: the layout is measured after both. */
export async function settle(doc, root) {
  await Promise.all([...root.querySelectorAll('img')].map(i => i.complete ? null : new Promise(r => { i.onload = r; i.onerror = r; })));
  if (!doc.fonts) return;
  // A face loads when text that uses it is drawn, and `ready` waits only for loads already started. Safari's engine had started none when the
  // new pages were measured, so its first drawing was measured in fallback faces and broke pages early (docs/BOOK_EXPORT.md 41.20). Every
  // face the page declares is loaded first (the theme's and the GM stylesheet's, all inline); a face that fails is measured without, as before.
  await Promise.all([...doc.fonts].map(f => f.load().catch(() => null)));
  await doc.fonts.ready;
}

// Absolutely placed art and Homebrewery's own end-of-page filler (an empty paragraph after a column break) never count.
function counts(win, el) {
  const cs = win.getComputedStyle(el);
  if (cs.position === 'absolute' || cs.position === 'fixed') return false;
  if (el.classList.contains('columnSplit')) return false;
  return !(el.tagName === 'P' && el.textContent.replace(/ /g, '').trim() === '' && !el.querySelector('img'));
}

function spills(win, page) {
  const box = page.getBoundingClientRect();
  const cs = win.getComputedStyle(page);
  // The preview shows the pages scaled down to fit (a transform, which changes no line); boxes are measured scaled, paddings are not.
  const scale = page.offsetWidth ? box.width / page.offsetWidth : 1;
  // The content box: a block that starts past its right edge sits in a column that does not exist (the page clips it).
  const left = box.left + parseFloat(cs.paddingLeft) * scale, right = box.right - parseFloat(cs.paddingRight) * scale, bottom = box.bottom - parseFloat(cs.paddingBottom) * scale;
  const rtl = cs.direction === 'rtl';
  // Each fragment on its own: a block split across columns has one box per column, and its overall box can sit inside the page while its
  // last fragment is in a column past the page's edge.
  return [...page.querySelector('.columnWrapper').children].filter(el => counts(win, el)).filter(el =>
    [...el.getClientRects()].some(r => (r.width > 0 || r.height > 0) && ((rtl ? r.right <= left + 1 : r.left >= right - 1) || r.bottom > bottom + 1)));
}

// A heading never ends a page with its text on the next: a run of headings just above the first block that moves goes with it. Not the page's
// first block, which would move the whole page.
function withHeadings(win, wrap, from) {
  const counted = [...wrap.children].filter(el => counts(win, el));
  let i = counted.indexOf(from);
  while (i > 1 && /^H[1-6]$/.test(counted[i - 1].tagName)) i--;
  return i > 0 ? counted[i] : from;
}

/**
 * Whatever spills past a page is moved onto a new page after it, until every page fits. It works on the drawn blocks, not the Markdown, so
 * what is stored is not moved; a block too tall for a page of its own stays, and is reported, never silently lost.
 * Returns the pages that still do not fit (their numbers). `moved`, if given, gets the first block of each page made.
 */
export function fit(doc, root, moved) {
  const win = doc.defaultView;
  let made = 0;
  for (let page = root.querySelector('.page'); page && made < 400; page = page.nextElementSibling) {
    const out = spills(win, page);
    if (!out.length) continue;
    const wrap = page.querySelector('.columnWrapper');
    if (out[0] === [...wrap.children].filter(el => counts(win, el))[0]) continue;
    const from = withHeadings(win, wrap, out[0]);
    const next = doc.createElement('div');
    next.className = page.className;
    next.dataset.fitted = '1';
    if (page.dataset.section) next.dataset.section = page.dataset.section;
    const into = doc.createElement('div');
    into.className = 'columnWrapper';
    next.appendChild(into);
    // The element moved first, and the page it came from (its classes are the new page's, as a written break would give them).
    if (moved) moved.push({ el: from, classes: [...page.classList].filter(c => c !== 'page') });
    for (let el = from; el;) { const n = el.nextElementSibling; into.appendChild(el); el = n; }
    page.after(next);
    made++;
  }
  const pages = [...root.querySelectorAll('.page')];
  pages.forEach((p, i) => { p.id = 'p' + (i + 1); });
  return pages.filter(p => spills(win, p).length > 0).map(p => pages.indexOf(p) + 1);
}

// The first words of a page's first block that does not fit, for the report.
function firstWords(win, page) {
  const el = spills(win, page)[0];
  const words = (el?.textContent || '').replace(/\s+/g, ' ').trim().split(' ').slice(0, 8).join(' ');
  return words.length > 80 ? words.slice(0, 80) + '\u2026' : words;
}

/** The pages that still do not fit, each with its chapter and the first words of what does not fit. */
function report(doc, root, pagesOut) {
  const pages = [...root.querySelectorAll('.page')];
  return pagesOut.map(n => ({ page: n, section: pages[n - 1]?.dataset.section || null, words: firstWords(doc.defaultView, pages[n - 1]) }));
}

/** A break line written by Fit pages: "\page {fitted}", or with the classes of the page it continues ("\page {reliquary,fitted}"). */
export function isFitted(line) {
  const m = /^\\page(?:break)? *\{([^\n{}]*)\}$/.exec(line.trim());
  return !!m && m[1].split(',').map(t => t.trim()).includes(FIT_CLASS);
}

/** A chapter without the breaks an earlier Fit pages wrote (each with the blank line it added); the GM's own breaks stay. */
export function unfit(markdown) {
  const lines = markdown.split('\n');
  const kept = [];
  for (let i = 0; i < lines.length; i++) {
    if (!isFitted(lines[i])) { kept.push(lines[i]); continue; }
    // The blank line written after it, when one stood before it.
    if (i + 1 < lines.length && lines[i + 1] === '' && (kept.length === 0 || kept[kept.length - 1] === '')) i++;
  }
  return kept.join('\n');
}

/**
 * `markdown` with a fitted break before each offset (each the start of a line). The break is a line of its own; after a blank line it is followed
 * by one too, so it stands apart, and `unfit` takes out exactly what was put in: the text without its breaks is the text as it was.
 */
export function insertBreaks(markdown, breaks) {
  let text = markdown;
  for (const { at, classes } of [...breaks].sort((a, b) => b.at - a.at)) {
    const line = '\\page {' + [...(classes || []), FIT_CLASS].join(',') + '}';
    const blank = at === 0 || text[at - 1] === '\n' && (at === 1 || text[at - 2] === '\n');
    text = text.slice(0, at) + line + (blank ? '\n\n' : '\n') + text.slice(at);
  }
  return text;
}

/**
 * Fit pages (docs/BOOK_EXPORT.md 41.15): the chapters with a "\page {fitted}" written where the drawing would otherwise move a block onto a
 * page of its own, so the stored Markdown is what prints. Breaks from an earlier run are taken out first. Laid out in `root` of `doc` (the
 * print browser), as the PDF is. Returns the chapters, the breaks it could not write (a block whose Markdown it could not find), and the pages
 * that still do not fit (a block taller than a page).
 */
export async function fitText(doc, root, chapters, art) {
  let current = chapters.map(c => ({ section: c.section, markdown: unfit(c.markdown) }));
  const unplaced = new Map();
  for (let pass = 0; pass < 6; pass++) {
    const marks = { nonce: 'f' + Math.random().toString(36).slice(2, 10), at: [] };
    draw(doc, root, current, art, marks);
    await settle(doc, root);
    const moved = [];
    fit(doc, root, moved);
    if (!moved.length) break;
    const map = brewMap(current);
    const breaks = new Map();
    for (const { el, classes } of moved) {
      // The block itself, or the nearest one before it on its page that has a place in the Markdown (the cleaner can leave a block untagged).
      let at = -1;
      const page = el.closest('.page');
      for (let e = el; e && at < 0; e = e.previousElementSibling) {
        const k = /^[^-]+-(\d+)$/.exec(e.dataset?.fm || '');
        if (k) at = marks.at[Number(k[1])] ?? -1;
      }
      const chapter = at < 0 ? null : map.find(c => at >= c.start && at <= c.start + c.length);
      const offset = chapter ? at - chapter.start : -1;
      // A break before the first line of the page's text would only make an empty page.
      const pageStart = chapter ? pageStartIn(current[map.indexOf(chapter)].markdown, offset) : 0;
      if (!chapter || offset <= pageStart) {
        const words = (el.textContent || '').replace(/\s+/g, ' ').trim().split(' ').slice(0, 8).join(' ');
        unplaced.set(words, { section: page?.dataset.section || null, words });
        continue;
      }
      const list = breaks.get(chapter.section) ?? [];
      if (!list.some(b => b.at === offset)) list.push({ at: offset, classes });
      breaks.set(chapter.section, list);
    }
    if (!breaks.size) break;
    current = current.map(c => breaks.has(c.section) ? { section: c.section, markdown: insertBreaks(c.markdown, breaks.get(c.section)) } : c);
  }
  draw(doc, root, current, art);
  await settle(doc, root);
  const overflow = report(doc, root, fit(doc, root));
  const runOn = root.querySelectorAll('.page[data-fitted]').length;
  return { chapters: current, unplaced: [...unplaced.values()], overflow, runOn, pages: root.querySelectorAll('.page').length };
}

// Where the page holding `offset` starts in a chapter's text: after the last break line before it, or 0.
function pageStartIn(markdown, offset) {
  let start = 0;
  for (const m of markdown.slice(0, offset).matchAll(/^\\page(?:break)?(?: *\{[^\n{}]*\})?$\n?/gm)) start = m.index + m[0].length;
  // Blank lines after the break are still the top of the page.
  while (start < offset && /\s/.test(markdown[start])) start++;
  return start;
}

/**
 * The number each page prints, as Homebrewery's Blank theme counts them (its `page-numbers` counter): every page counts, except one with
 * `.skipCounting`, and a page with `.resetCounting` is page 1.
 */
export function pageNumbers(root) {
  let n = 0;
  return [...root.querySelectorAll('.page')].map(p => {
    if (p.querySelector('.resetCounting')) n = 1;
    else if (!p.querySelector('.skipCounting')) n++;
    return n;
  });
}

/**
 * Each page gets Homebrewery's page number (`<div class="pageNumber auto">`, which a theme places and fills from its counter), unless the
 * text already wrote one or the page is a cover. It sits beside the columns, not in them, so moving spilled blocks never takes it away.
 */
export function numberPages(root) {
  for (const page of root.querySelectorAll('.page')) {
    if (page.querySelector('.pageNumber, .frontCover, .insideCover, .backCover')) continue;
    const n = page.ownerDocument.createElement('div');
    n.className = 'pageNumber auto';
    page.append(n);
  }
}

const COVERS = '.frontCover, .insideCover, .backCover, .partCover';

/**
 * A running footer (docs/BOOK_EXPORT.md 28.4): each page gets Homebrewery's footnote (`<div class="footnote auto">`, which a theme places at
 * the page's foot, beside the page number) with the name of the chapter it is in, read from the drawn page's own `h1`, so an edition can
 * only ever show its own text. A page takes the first chapter title on it, or else the last one before it. Covers and the contents get none,
 * nor does a page whose text wrote its own footnote. It sits beside the columns, not in them, so it takes no room from the text.
 */
export function footPages(root) {
  let current = '';
  for (const page of root.querySelectorAll('.page')) {
    if (page.querySelector(COVERS)) continue;
    const titles = [...page.querySelectorAll('h1')].filter(h => !h.closest('.toc')).map(h => h.textContent.trim()).filter(Boolean);
    const shown = titles[0] || current;
    if (titles.length) current = titles[titles.length - 1];
    if (!shown || page.querySelector('.footnote, .toc')) continue;
    const f = page.ownerDocument.createElement('div');
    f.className = 'footnote auto';
    f.textContent = shown;
    page.append(f);
  }
}

/**
 * Each contents line gets the page its chapter landed on, read from the laid-out pages, in the shape Homebrewery's contents take: the line's
 * words in one span and the number in a second, which Blank's `.toc` rules join with dot leaders.
 */
export function number(root) {
  const pages = [...root.querySelectorAll('.page')];
  const numbers = pageNumbers(root);
  for (const a of root.querySelectorAll('.toc a[href^="#"]')) {
    const target = root.querySelector('#' + CSS.escape(a.getAttribute('href').slice(1)));
    const page = target && target.closest('.page');
    // A contents the brew wrote itself, in Homebrewery's shape (`[{{ Title}}{{ 5}}](#p5)`: the words in one span and an authored number in a
    // last one), already has its number's place, which its theme's leader rules (`span:first-child::after`) are written against. The number
    // is then put into that span, true to the page the chapter landed on, rather than a second one added after it.
    const own = [...a.children].filter(c => c.tagName === 'SPAN' && !c.matches('.toc-words, .toc-page'));
    const authored = own.length > 1 && own.length === a.children.length && /^\s*\d+\s*$/.test(own[own.length - 1].textContent) ? own[own.length - 1] : null;
    if (authored) {
      if (page) authored.textContent = String(numbers[pages.indexOf(page)]);
      continue;
    }
    a.querySelectorAll('.toc-page').forEach(s => s.remove());
    if (!a.querySelector(':scope > span.toc-words')) {
      const words = a.ownerDocument.createElement('span');
      words.className = 'toc-words';
      words.append(...a.childNodes);
      a.append(words);
    }
    if (!page) continue;
    const n = a.ownerDocument.createElement('span');
    n.className = 'toc-page';
    n.textContent = String(numbers[pages.indexOf(page)]);
    a.append(n);
  }
}

/** Draws, settles, fits and numbers: what the preview shows and the PDF prints. `input` is the brew or the chapters (see `draw`). */
export async function layout(doc, root, input, art) {
  draw(doc, root, input, art);
  await settle(doc, root);
  const overflow = report(doc, root, fit(doc, root));
  const runOn = root.querySelectorAll('.page[data-fitted]').length;
  numberPages(root);
  footPages(root);
  number(root);
  return { pages: root.querySelectorAll('.page').length, overflow, runOn };
}

/** For the print browser: the book laid out in this page, and the page as it then is, to be printed with script off. */
export async function print(input, art) {
  const root = document.getElementById('brew');
  const result = await layout(document, root, input, art);
  return { html: '<!doctype html>\n' + document.documentElement.outerHTML, pages: result.pages, overflow: result.overflow };
}

/** For the print browser: Fit pages on the draft's chapters (`fitText`), laid out in this page. */
export async function fitPages(chapters, art) {
  return fitText(document, document.getElementById('brew'), chapters, art);
}
