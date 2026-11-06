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
// Nothing in a style may load or run: no url(), no image functions, no escapes that could spell one, no legacy script hooks.
const UNSAFE_STYLE = /url\s*\(|image-set|image\s*\(|element\s*\(|cross-fade|expression|behavior|binding|@import|\\|javascript:|src\s*\(/i;

/** The draft as one brew: its chapters in order, each starting a page. */
export function brew(chapters) {
  return chapters.map(c => c.markdown.replace(/\s+$/, '')).join('\n\n\\page\n\n');
}

/** Homebrewery's pages, as marked-hbfm renders them, before cleaning. */
export function pagesHtml(markdown) {
  const pages = markdown.split(PAGE_BREAK);
  // Twice when the brew has variables: marked-hbfm learns a variable set on a later page only by rendering that page, and Homebrewery's editor,
  // rendering on every keystroke, always has. The print browser renders once, so without the first pass its pages would not match the preview.
  if (markdown.includes('[')) renderPages(pages);
  return renderPages(pages);
}

function renderPages(pages) {
  return pages.map((text, i) => {
    let tags = { classes: '', style: '' };
    if (text.startsWith('\\page')) {
      tags = pageTags(text.split('\n', 1)[0]);
      text = text.includes('\n') ? text.substring(text.indexOf('\n') + 1) : '';
    }
    // Homebrewery's own filler: an empty last column, so a page's columns fill from the left.
    text += '\n\n&nbsp;\n\\column\n&nbsp;';
    let html;
    try {
      html = hbfm.render(text, i);
    } catch (e) {
      html = '<p class="render-failed"></p>';
    }
    const style = tags.style ? ` style="${tags.style.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;')}"` : '';
    return `<div class="page${tags.classes}"${style} id="p${i + 1}"><div class="columnWrapper">${html}</div></div>`;
  }).join('');
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
  const classes = String(injected.classes || '').split(/\s+/).filter(c => /^[A-Za-z][\w-]{0,40}$/.test(c) && c !== 'page').map(c => ' ' + c).join('');
  const style = injected.styles ? Object.entries(injected.styles).map(([k, v]) => `${k}:${v}`).join(';') : '';
  return { classes, style };
}

/** The pages, cleaned, as nodes of `doc`. Parsed inert (a template's content runs nothing and loads nothing), then walked. */
export function clean(doc, html) {
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
          attributes(child);
          if (tag === 'img' && !child.hasAttribute('data-art')) child.remove();
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

function attributes(el) {
  const tag = el.localName;
  const src = tag === 'img' ? el.getAttribute('src') : null;
  for (const { name, value } of [...el.attributes]) {
    const keep = ATTRIBUTES.has(name)
      && !(name === 'href' && !/^#[\w-]{1,120}$/.test(value))
      && !(name === 'style' && UNSAFE_STYLE.test(value))
      && !((name === 'width' || name === 'height') && !/^\d{1,4}%?$/.test(value))
      && !(name === 'id' && !/^[\w-]{1,120}$/.test(value))
      && !(name === 'class' && !/^[\w -]{0,300}$/.test(value));
    if (!keep) el.removeAttribute(name);
  }
  if (tag === 'img') {
    const slot = src && SLOT.exec(src.trim());
    if (slot) el.setAttribute('data-art', slot[1]);
  }
  // A link only within the book, and one that goes nowhere stays as its words.
  if (tag === 'a' && !el.hasAttribute('href')) el.setAttribute('data-plain', '');
}

/** The edition's pictures in place: a slot it has a picture for shows it, one it has none for shows a marked empty box (not printed). */
export function pictures(root, art) {
  for (const img of root.querySelectorAll('img[data-art]')) {
    const uri = art && Object.prototype.hasOwnProperty.call(art, img.dataset.art) ? art[img.dataset.art] : null;
    if (typeof uri === 'string' && /^data:image\/(png|jpeg|webp|gif);base64,[A-Za-z0-9+/=]+$/.test(uri)) {
      img.src = uri;
    } else {
      const box = img.ownerDocument.createElement('span');
      box.className = 'art-missing';
      box.textContent = img.alt || '';
      img.replaceWith(box);
    }
  }
}

/** The book drawn into `root` (a div of `doc`). */
export function draw(doc, root, markdown, art) {
  const pages = doc.createElement('div');
  pages.className = 'pages';
  pages.append(doc.importNode(clean(doc, pagesHtml(markdown)), true));
  pictures(pages, art);
  root.replaceChildren(pages);
  return pages;
}

/** Waits for the pictures and the fonts: the layout is measured after both. */
export async function settle(doc, root) {
  await Promise.all([...root.querySelectorAll('img')].map(i => i.complete ? null : new Promise(r => { i.onload = r; i.onerror = r; })));
  if (doc.fonts) await doc.fonts.ready;
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

/**
 * Whatever spills past a page is moved onto a new page after it, until every page fits. It works on the drawn blocks, not the Markdown, so
 * what is stored is not moved; a block too tall for a page of its own stays, and is reported, never silently lost.
 * Returns the pages that still do not fit (their numbers).
 */
export function fit(doc, root) {
  const win = doc.defaultView;
  let made = 0;
  for (let page = root.querySelector('.page'); page && made < 400; page = page.nextElementSibling) {
    const out = spills(win, page);
    if (!out.length) continue;
    const wrap = page.querySelector('.columnWrapper');
    if (out[0] === [...wrap.children].filter(el => counts(win, el))[0]) continue;
    const next = doc.createElement('div');
    next.className = page.className;
    next.dataset.fitted = '1';
    const into = doc.createElement('div');
    into.className = 'columnWrapper';
    next.appendChild(into);
    for (let el = out[0]; el;) { const n = el.nextElementSibling; into.appendChild(el); el = n; }
    page.after(next);
    made++;
  }
  const pages = [...root.querySelectorAll('.page')];
  pages.forEach((p, i) => { p.id = 'p' + (i + 1); });
  return pages.filter(p => spills(win, p).length > 0).map(p => pages.indexOf(p) + 1);
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

/** Draws, settles, fits and numbers: what the preview shows and the PDF prints. */
export async function layout(doc, root, markdown, art) {
  draw(doc, root, markdown, art);
  await settle(doc, root);
  const overflow = fit(doc, root);
  numberPages(root);
  number(root);
  return { pages: root.querySelectorAll('.page').length, overflow };
}

/** For the print browser: the book laid out in this page, and the page as it then is, to be printed with script off. */
export async function print(markdown, art) {
  const root = document.getElementById('brew');
  const result = await layout(document, root, markdown, art);
  return { html: '<!doctype html>\n' + document.documentElement.outerHTML, pages: result.pages, overflow: result.overflow };
}
