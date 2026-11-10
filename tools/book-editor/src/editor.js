// The book editor's browser half (docs/BOOK_EXPORT.md 41): CodeMirror for a chapter's Markdown and for the GM's stylesheet, and the live
// preview of the whole book in a sandboxed frame. The server owns the draft: this module holds the chapters' text while the dialog is open,
// tells the server when a chapter changed (a small call), and hands the text over as a stream when the server asks for it, so a long
// chapter never has to fit in one message.
import { EditorView, basicSetup } from 'codemirror';
import { EditorState, Compartment, StateField, StateEffect } from '@codemirror/state';
import { keymap, gutter, GutterMarker } from '@codemirror/view';
import { startCompletion } from '@codemirror/autocomplete';
import { indentWithTab } from '@codemirror/commands';
import { markdown } from '@codemirror/lang-markdown';
import { css } from '@codemirror/lang-css';
import { HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { tags } from '@lezer/highlight';
import { unifiedMergeView, getOriginalDoc, getChunks, acceptChunk, rejectChunk, goToNextChunk, goToPreviousChunk } from '@codemirror/merge';
import { reviewDiffConfig } from './review-diff.js';
import { layout, brew, brewMap, outlineOf, pageStarts, pageNumbers } from './render.js';
import { assist } from './assist.js';
import { chips, setChips, placeEdit, referenced } from './chips.js';

// The Markdown's and the stylesheet's colours from the app's own palette (CSS variables), so the editor is dark or light as the app is.
const bookHighlight = syntaxHighlighting(HighlightStyle.define([
  { tag: tags.heading, color: 'var(--accent)', fontWeight: 'bold' },
  { tag: [tags.link, tags.url], color: 'var(--arcane)' },
  { tag: tags.emphasis, fontStyle: 'italic' },
  { tag: tags.strong, fontWeight: 'bold' },
  { tag: [tags.processingInstruction, tags.meta, tags.contentSeparator, tags.labelName], color: 'var(--text-3)' },
  { tag: tags.quote, color: 'var(--text-2)' },
  { tag: [tags.keyword, tags.propertyName, tags.tagName], color: 'var(--accent)' },
  { tag: [tags.string, tags.number, tags.atom, tags.color], color: 'var(--arcane)' },
  { tag: tags.comment, color: 'var(--text-3)', fontStyle: 'italic' }
]));

const editors = new Map();
let counter = 0;

function debounce(fn, ms) {
  let t = 0;
  const run = (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
  run.now = (...a) => { clearTimeout(t); fn(...a); };
  run.cancel = () => clearTimeout(t);
  return run;
}

/**
 * Opens the editor in `host`, the preview in `frame`. `book` = { chapters: [{section, markdown}], css, shell, artUrl, current, rtl, assist }.
 * `book.assist` = { on, help, slots, dm }: the suggestions and hover help (assist.js), and whether they start on.
 * `dotnet` gets Changed(section) after typing stops, and CssChanged() for the stylesheet.
 */
export async function open(host, frame, book, dotnet, labels) {
  // One editor per host: one still opening (a theme picked twice in quick succession) is closed before another is made, or both would stay.
  for (const [other, s] of editors) if (s.host === host) close(other);
  const id = 'e' + (++counter);
  const state = { host, frame, dotnet, chapters: book.chapters.map(c => ({ ...c })), css: book.css || '', current: book.current, mode: 'md',
    art: {}, rendering: false, again: false, labels: labels || {}, follow: book.follow !== false, zoom: book.zoom === '100' ? '100' : 'fit',
    outlines: new Map(), sent: new Map(), dom: null, aimed: null, breakPages: [] };
  editors.set(id, state);
  state.id = id;
  state.dm = !!book.assist?.dm;

  const language = new Compartment();
  const locked = new Compartment();
  const themeLock = new Compartment();
  const assisting = new Compartment();
  // The chips under the entries' headings and the names after "art:" (docs/BOOK_EXPORT.md 53) come and go with the suggestions and help.
  state.chipData = book.chips || null;
  const spot = book.assist?.help?.spot;
  const names = new Map((book.assist?.slots || []).map(s => [s.key, s.kind === 'spot' && spot ? spot.replace('{0}', s.label) : s.label]));
  const chipping = chips({
    // The editor's own lock (an AI edit asked for or under review) greys the chips' buttons too.
    data: () => state.chipData ? { ...state.chipData, locked: !!state.lockedOn } : null,
    labels: () => state.labels.chip || {},
    names,
    act: (kind, key) => dotnet.invokeMethodAsync('Chip', kind, key),
    quote: key => dotnet.invokeMethodAsync('ChipQuote', key),
    upload: key => {
      // The page's own file field takes the picture (a stream, as the tray's does); the server is told first which place it is for.
      const field = document.getElementById('book-chip-file');
      if (!field) return;
      field.value = '';
      dotnet.invokeMethodAsync('ChipUploadFor', key);
      field.click();
    },
    place: chip => placeArt(id, state.current, chip.key, chip.block)
  });
  const assistance = book.assist ? [...assist(book.assist.help || {}, book.assist.slots || [], !!book.assist.dm), ...chipping] : [];
  state.assistance = assistance;
  state.assistOn = !!book.assist?.on;
  // A Hebrew draft: each line takes its own direction from its first letters, so Hebrew prose reads from the right and a line of markup
  // ({{frontCover}}, \page, an entry's marker) reads as written instead of mirrored around its punctuation.
  // (plaintext bidi sets each line's direction; perLineTextDirection makes CodeMirror read it back for the cursor and selection.)
  const direction = book.rtl
    ? [EditorView.contentAttributes.of({ dir: 'rtl' }), EditorView.perLineTextDirection.of(true), EditorView.theme({ '.cm-line': { unicodeBidi: 'plaintext' } })]
    : [];
  // What was typed and not yet handed to the server: chapters by section, and the stylesheet.
  state.pending = new Set();
  state.cssPending = false;
  const changed = debounce(() => {
    const sections = [...state.pending];
    const sheet = state.cssPending;
    state.pending.clear();
    state.cssPending = false;
    for (const section of sections) dotnet.invokeMethodAsync('Changed', section);
    if (sheet) dotnet.invokeMethodAsync('CssChanged');
  }, 900);
  const repaint = debounce(() => preview(id), 350);
  // A wider or narrower frame (the window resized) scales the pages again; nothing is drawn again.
  new ResizeObserver(() => { const r = frame.contentDocument?.getElementById('brew'); if (r) fitWidth(frame, r, state.zoom); }).observe(frame);
  state.changed = changed;
  const outlineSoon = debounce(() => reportOutline(state, state.current), 300);
  const cursorMoved = debounce(() => followCursor(state), 200);
  state.outlineSoon = outlineSoon;
  state.view = new EditorView({
    parent: host,
    state: EditorState.create({
      doc: text(state),
      extensions: [basicSetup, bookHighlight, keymap.of([indentWithTab]), EditorView.lineWrapping, direction, language.of(markdown()), locked.of([]), themeLock.of([]), pageGutter(state), assisting.of(book.assist?.on ? assistance : []), helpTheme,
        EditorView.updateListener.of(u => {
          // A chapter put into the editor (shown, replaced by the server) is not typing: it must not be saved back.
          if (u.selectionSet && !state.loading && !state.review) cursorMoved();
          if (!u.docChanged || state.loading) return;
          const value = u.state.doc.toString();
          // The theme's sheet is read-only; nothing typed there (there is no way to) is ever written to a chapter or the GM's sheet.
          if (state.mode === 'theme') return;
          if (state.mode === 'css') { state.css = value; state.cssPending = true; } else { chapter(state).markdown = value; state.pending.add(state.current); outlineSoon(); }
          changed();
          repaint();
        })]
    })
  });
  state.language = language;
  state.locked = locked;
  state.themeLock = themeLock;
  state.assisting = assisting;
  state.direction = direction;

  // The frame is sandboxed with no script; allow-same-origin only so this page can draw into it and measure what it drew.
  frame.srcdoc = book.shell;
  await new Promise(r => frame.addEventListener('load', r, { once: true }));
  try {
    const answer = await fetch(book.artUrl, { credentials: 'same-origin', cache: 'no-store' });
    if (answer.ok) {
      // The pictures by slot, and the ones the book leaves out (the Problems tab names them).
      const got = await answer.json();
      state.art = got.art || {};
      dotnet.invokeMethodAsync('ArtLeftOut', got.leftOut || []);
    }
  } catch {
    // No pictures in the preview; the book still draws, with empty boxes where they go.
  }
  listen(state);
  for (const c of state.chapters) reportOutline(state, c.section);
  await preview(id);
  scrollTo(state, state.current);
  return id;
}

// The chapters as the preview draws them: during a review, the chapter with only the changes accepted so far.
function shown(state) {
  const r = state.review;
  if (!r) return state.chapters;
  return state.chapters.map(c => c.section !== r.section ? c
    : { ...c, markdown: c.markdown.slice(0, r.from) + getOriginalDoc(r.view.state).toString() + c.markdown.slice(r.to) });
}

function chapter(state) {
  return state.chapters.find(c => c.section === state.current) || { markdown: '' };
}

function text(state) {
  return state.mode === 'css' ? state.css : state.mode === 'theme' ? themeText(state) : chapter(state).markdown;
}

// The active theme's sheet, as the preview's frame holds it, without what cannot be copied: faces and pictures are data in the sheet, thousands
// of characters each, and the server adds a theme's faces itself. Laid out over lines when the sheet came as one.
function readable(css) {
  let out = css.replace(/@font-face\s*{[^{}]*}/g, '').replace(/[^;{}]*url\(\s*["']?data:[^)]*\)[^;}]*;?/gi, '');
  if (out.split('\n').length < 20) out = out.replace(/\s*{\s*/g, ' {\n  ').replace(/;\s*(?!})/g, ';\n  ').replace(/\s*}\s*/g, '\n}\n');
  return out.replace(/\n{3,}/g, '\n\n').trim() + '\n';
}

function themeText(state) {
  const el = state.frame.contentDocument?.getElementById('theme');
  return readable(el?.textContent || '');
}

/** The theme's rules added at the end of the GM's own sheet, as one edit that saves like typing (the sheet is never replaced). */
export function copyTheme(id) {
  const state = editors.get(id);
  if (!state) return false;
  const note = state.labels.copied || 'Copied from the theme';
  state.css = (state.css.replace(/\s+$/, '') + `\n\n/* ${note} */\n` + themeText(state)).replace(/^\s+/, '');
  state.cssPending = true;
  state.changed();
  preview(id);
  return true;
}

function load(state, value) {
  state.loading = true;
  try {
    state.view.dispatch({ changes: { from: 0, to: state.view.state.doc.length, insert: value } });
  } finally {
    state.loading = false;
  }
}

/** Suggestions and hover help on or off (docs/BOOK_EXPORT.md 47), at once, in the open editor. The stylesheet never has them: they are the book's
 * blocks, not CSS. */
export function setAssist(id, on) {
  const state = editors.get(id);
  if (!state) return;
  state.assistOn = !!on;
  state.view.dispatch({ effects: state.assisting.reconfigure(on && state.mode === 'md' ? state.assistance : []) });
  ghosts(state);
}

/**
 * The entries' picture places as the server reads them now (docs/BOOK_EXPORT.md 53): `data` = { chips: [{key, section, label, block, pictured,
 * idea}], model, price, drawAsk, busy, locked }. The chips under the headings and the ghost frames in the preview are drawn from it.
 */
export function setChipData(id, data) {
  const state = editors.get(id);
  if (!state) return;
  state.chipData = data;
  state.view.dispatch({ effects: setChips.of(null) });
  ghosts(state);
}

/**
 * Puts a place's picture block under its entry's heading, as one edit that saves and undoes like typing. 'placed'; 'present' when the chapter
 * already points at the picture or has no such entry; 'busy' when the chapter is locked or under review (the chip then offers to put it in).
 */
export function placeArt(id, section, key, block) {
  const state = editors.get(id);
  const c = state?.chapters.find(x => x.section === section);
  if (!c) return 'present';
  const edit = placeEdit(c.markdown, key, block);
  if (!edit) return 'present';
  if (state.mode === 'md' && state.current === section) {
    if (state.review || state.lockedOn) return 'busy';
    state.view.dispatch({ changes: { from: edit.at, insert: edit.insert }, userEvent: 'input.art' });
    return 'placed';
  }

  // A chapter that is not the one shown: its text takes the block and is saved like typing (there is no editor history to undo it from).
  if (state.review?.section === section) return 'busy';
  c.markdown = c.markdown.slice(0, edit.at) + edit.insert + c.markdown.slice(edit.at);
  state.pending.add(section);
  state.changed();
  reportOutline(state, section);
  preview(id);
  return 'placed';
}

// Where a suggested picture would sit, as a dashed frame over the page (docs/BOOK_EXPORT.md 53). It is laid over the page, outside its text, so
// no line moves and the pages stay the ones the PDF prints; it is drawn here only, after the layout, and is in neither the text nor the PDF.
function ghosts(state) {
  const doc = state.frame.contentDocument;
  const root = doc?.getElementById('brew');
  if (!root) return;
  for (const old of root.querySelectorAll('[data-ghost]')) old.remove();
  const data = state.chipData;
  if (!state.assistOn || !data?.chips?.length) return;
  const text = new Map(shown(state).map(c => [c.section, c.markdown]));
  for (const chip of data.chips) {
    if (chip.pictured || !chip.idea) continue;
    const markdown = text.get(chip.section);
    if (markdown == null || referenced(markdown, chip.key)) continue;
    const mark = doc.getElementById('e-' + chip.key);
    const page = mark?.closest('.page');
    if (!page || !page.offsetWidth) continue;
    // The entry's heading follows its marker; the frame starts under it.
    let block = mark;
    while (block.parentElement && !block.parentElement.classList.contains('columnWrapper') && block.parentElement !== page) block = block.parentElement;
    const heading = block.nextElementSibling && /^H[1-6]$/.test(block.nextElementSibling.tagName) ? block.nextElementSibling : block;
    const scale = page.getBoundingClientRect().width / page.offsetWidth || 1;
    const p = page.getBoundingClientRect();
    const h = heading.getBoundingClientRect();
    const column = h.width / scale;
    // The size the built-in themes give the block: a portrait floats at the far side, 38% of the column and at most 32 mm, three by four; a
    // place's picture is the column wide and at most 62 mm tall.
    const mm = 96 / 25.4;
    const portrait = chip.block.startsWith('{{portrait');
    const width = portrait ? Math.min(column * 0.38, 32 * mm) : column;
    let height = portrait ? width * 4 / 3 : Math.min(width * 0.5, 62 * mm);
    const top = (h.bottom - p.top) / scale + 2;
    // No line moves for a frame, so it keeps to its own entry: it ends where the next entry on the page starts below it.
    for (const next of page.querySelectorAll('[id^="e-"]')) {
      if (!(mark.compareDocumentPosition(next) & 4)) continue;
      const nextTop = (next.getBoundingClientRect().top - p.top) / scale;
      if (nextTop > top + 1) height = Math.max(6 * mm, Math.min(height, nextTop - top - 2));
      break;
    }
    const rtl = doc.defaultView.getComputedStyle(page).direction === 'rtl';
    const left = (h.left - p.left) / scale + (portrait && !rtl ? column - width : 0);
    const ghost = doc.createElement('div');
    ghost.dataset.ghost = chip.key;
    ghost.textContent = chip.label;
    const s = ghost.style;
    s.position = 'absolute';
    s.left = left + 'px';
    s.top = top + 'px';
    s.width = width + 'px';
    s.height = height + 'px';
    s.boxSizing = 'border-box';
    s.border = '2px dashed rgba(90, 90, 90, .75)';
    s.borderRadius = '2mm';
    s.background = 'rgba(255, 255, 255, .35)';
    s.color = 'rgba(60, 60, 60, .9)';
    s.font = '7pt sans-serif';
    s.padding = '1mm';
    s.textAlign = 'center';
    s.pointerEvents = 'none';
    s.zIndex = '20';
    s.overflow = 'hidden';
    page.append(ghost);
  }
}

// The hover help's box: the editor's own colours, a readable width.
const helpTheme = EditorView.theme({ '.cm-book-help': { padding: '4px 8px', maxWidth: '22rem', fontSize: '.85em', lineHeight: '1.35' } });

/** What is waiting to be saved, taken out of the queue: the server saves it itself, and knows when it is done (before closing, printing,
 * or acting on a chapter). */
export function flush(id) {
  const state = editors.get(id);
  if (!state) return { sections: [], css: false };
  state.changed.cancel();
  const result = { sections: [...state.pending], css: state.cssPending };
  state.pending.clear();
  state.cssPending = false;
  return result;
}

/** The edition's pictures read again (one was put in or taken out in the dock), and the preview drawn with them. */
export async function refreshArt(id, artUrl) {
  const state = editors.get(id);
  if (!state) return;
  try {
    const answer = await fetch(artUrl, { credentials: 'same-origin', cache: 'no-store' });
    if (!answer.ok) return;
    const got = await answer.json();
    state.art = got.art || {};
    state.dotnet.invokeMethodAsync('ArtLeftOut', got.leftOut || []);
  } catch {
    return;
  }
  await preview(id);
}

/** Shows a chapter's text (`tab` 'text'), the GM's stylesheet ('style', or `section` null) or the theme's sheet, read-only ('theme'). */
export function show(id, section, tab) {
  const state = editors.get(id);
  if (!state) return;
  const was = state.mode;
  state.mode = tab === 'theme' ? 'theme' : tab === 'style' || (section == null && !tab) ? 'css' : 'md';
  if (section != null) state.current = section;
  state.view.dispatch({ effects: [state.language.reconfigure(state.mode === 'md' ? markdown() : css()),
    state.assisting.reconfigure(state.assistOn && state.mode === 'md' ? state.assistance : []),
    state.themeLock.reconfigure(state.mode === 'theme' ? [EditorState.readOnly.of(true), EditorView.editable.of(false)] : [])] });
  load(state, text(state));
  if (state.mode !== 'theme') state.view.focus();
  if (state.mode === 'md') {
    refreshBreaks(state);
    if (was !== 'md' || section != null) scrollTo(state, state.current);
  }
}

/** The server replaced a chapter (Regenerate, undo, another tab): the editor and the preview take its text. */
export async function replace(id, section, markdown) {
  const state = editors.get(id);
  if (!state) return;
  // The chapter under review was replaced: the review is of text that is gone.
  if (state.review?.section === section) finish(id, 'none');
  const c = state.chapters.find(x => x.section === section);
  if (c) c.markdown = markdown; else state.chapters.push({ section, markdown });
  // What the server sent is now the text: nothing of this chapter is waiting to be saved.
  state.pending.delete(section);
  if (state.mode === 'md' && state.current === section) load(state, markdown);
  reportOutline(state, section);
  await preview(id);
}

/** The chapters changed as a set (a chapter added or removed): order and text. */
export async function chapters(id, list) {
  const state = editors.get(id);
  if (!state) return;
  state.chapters = list.map(c => ({ ...c }));
  for (const c of state.chapters) reportOutline(state, c.section);
  await preview(id);
}

/** The chapters' titles, as the overflow line names a page (a chapter added, renamed or removed). */
export function titles(id, map) {
  const state = editors.get(id);
  if (state) state.labels.titles = map;
}

/** A chapter's text (or the stylesheet's, `section` null). */
function textOf(id, section) {
  const state = editors.get(id);
  if (!state) throw new Error('No such editor.');
  return section == null ? state.css : (state.chapters.find(c => c.section === section) || { markdown: '' }).markdown;
}

/** How long the text is: Blazor takes no stream of no bytes, so empty text (the GM deleted it all) is read as this, not as a stream. */
export function length(id, section) {
  return textOf(id, section).length;
}

/** The text as bytes, for the server to read as a stream. */
export function read(id, section) {
  return new TextEncoder().encode(textOf(id, section));
}

/** A text field's value as bytes, read by the server as a stream (the theme wizard's pasted stylesheet: a message to the server is capped at
 * 32 KB, and a theme with its fonts written in is far more). */
export function fieldBytes(field) {
  return new TextEncoder().encode(field.value);
}

/** A text field filled from the server (a theme opened for editing). */
export function setField(field, value) {
  field.value = value;
}

/** The GM's stylesheet, cleaned by the server, as the preview should apply it. */
export function style(id, cleaned) {
  const state = editors.get(id);
  if (!state) return;
  const doc = state.frame.contentDocument;
  const el = doc && doc.getElementById('gm-css');
  if (el) el.textContent = cleaned;
  preview(id);
}

async function preview(id) {
  const state = editors.get(id);
  if (!state) return;
  // One drawing at a time; typing during one asks for one more after it, not one per key.
  if (state.rendering) { state.again = true; return; }
  state.rendering = true;
  try {
    const doc = state.frame.contentDocument;
    const root = doc && doc.getElementById('brew');
    if (!root) return;
    const scroller = doc.scrollingElement;
    const top = scroller ? scroller.scrollTop : 0;
    const result = await layout(doc, root, shown(state), state.art);
    fitWidth(state.frame, root, state.zoom);
    if (scroller) scroller.scrollTop = top;
    mapDom(state, root);
    ghosts(state);
    const box = state.host.closest('[data-book-editor]');
    const status = box?.querySelector('[data-overflow]');
    if (status) {
      // Each page that prints cut off, with its chapter: "page 7 (Cast)".
      const titles = state.labels.titles || {};
      const pages = result.overflow.map(o => o.section && titles[o.section] ? `${o.page} (${titles[o.section]})` : String(o.page));
      status.textContent = pages.length ? (state.labels.overflow || '').replace('{0}', pages.join(', ')) : '';
      status.hidden = pages.length === 0;
    }
    // Pages the drawing continued by itself: Fit pages would write those breaks into the text.
    const runOn = box?.querySelector('[data-run-on]');
    if (runOn) {
      runOn.textContent = result.runOn ? (state.labels.runOn || '').replace('{0}', String(result.runOn)) : '';
      runOn.hidden = !result.runOn;
    }
  } finally {
    state.rendering = false;
    if (state.again) { state.again = false; preview(id); }
  }
}

// The pages scaled to the frame's width. A transform moves nothing on the page (the fit above measured the same lines); the space the
// unscaled pages would take below is given back.
/**
 * The theme wizard's preview (docs/BOOK_EXPORT.md 41.22): `shell` drawn in `frame` with `markdown` laid out in it, no editor and no pictures.
 * The frame is sandboxed with no script, as the editor's is; this page draws into it.
 */
export async function sample(frame, shell, markdown) {
  frame.srcdoc = shell;
  await new Promise(r => frame.addEventListener('load', r, { once: true }));
  const doc = frame.contentDocument;
  const root = doc && doc.getElementById('brew');
  if (!root) return 0;
  const result = await layout(doc, root, markdown, {});
  fitWidth(frame, root);
  return result.pages;
}

function fitWidth(frame, root, zoom) {
  const page = root.querySelector('.page');
  if (!page) return;
  const scale = zoom === '100' ? 1 : Math.min(1, (frame.clientWidth - 24) / page.offsetWidth);
  root.style.transformOrigin = 'top center';
  root.style.transform = scale < 1 ? `scale(${scale})` : '';
  root.style.marginBottom = scale < 1 ? `${-(1 - scale) * root.offsetHeight}px` : '';
}

function scrollTo(state, section) {
  const doc = state.frame.contentDocument;
  const target = doc && doc.getElementById('ch-' + section);
  if (target) target.closest('.page')?.scrollIntoView({ block: 'start' });
  else if (doc && doc.scrollingElement && section === 'front') doc.scrollingElement.scrollTop = 0;
}

// ---- Outline, sync between the text and the pages, snippets (docs/BOOK_EXPORT.md 52) ----

// The headings of a chapter, read again only when its text changed.
function outlineFor(chapter) {
  const cached = state_outline.get(chapter.section);
  if (cached && cached.text === chapter.markdown) return cached.list;
  const list = outlineOf(chapter.markdown);
  state_outline.set(chapter.section, { text: chapter.markdown, list });
  return list;
}
const state_outline = new Map();

const MAX_HEADINGS = 150;

/** Tells the server a chapter's `##` and `###` headings (title and place in the list), when they changed since it was last told. */
function reportOutline(state, section) {
  const c = state.chapters.find(x => x.section === section);
  if (!c) return;
  const all = outlineFor(c);
  const items = [];
  all.forEach((h, index) => { if ((h.level === 2 || h.level === 3) && items.length < MAX_HEADINGS) items.push({ level: h.level, text: h.text, index }); });
  const key = JSON.stringify(items);
  if (state.sent.get(section) === key) return;
  state.sent.set(section, key);
  state.dotnet.invokeMethodAsync('Outline', section, items);
}

// The heading elements of the drawn book, and the pages with the number each prints; the chapters' headings counted in the same order.
function mapDom(state, root) {
  const pages = [...root.querySelectorAll('.page')];
  // The contents' own heading counts too: the outline reads it as a heading, and so must this list, or every heading after it is one off.
  const headings = [...root.querySelectorAll('h1,h2,h3,h4,h5,h6')];
  const numbers = pageNumbers(root);
  const bases = new Map();
  let n = 0;
  for (const c of shown(state)) { bases.set(c.section, n); n += outlineFor(c).length; }
  state.dom = { root, pages, headings, numbers, bases };
  // An aim taken at the old drawing (a heading just typed was not in it yet) does not count for this one.
  state.domVersion = (state.domVersion || 0) + 1;
  refreshBreaks(state);
  pageStatus(state);
}

function headingElement(state, section, index) {
  const dom = state.dom;
  if (!dom || !dom.headings.length) return null;
  const base = dom.bases.get(section);
  if (base === undefined) return null;
  // The counts can disagree (a heading the drawing makes or drops); the nearest one is the answer, never an error.
  return dom.headings[Math.min(dom.headings.length - 1, base + index)];
}

function scrollPreviewTo(state, el) {
  if (!el) return;
  el.scrollIntoView({ block: 'start' });
}

// The printed number of each "\page" line of the chapter shown, for the gutter.
function refreshBreaks(state) {
  const dom = state.dom;
  const numbers = [];
  if (dom && state.mode === 'md') {
    const chapters = shown(state);
    const map = brewMap(chapters);
    const at = chapters.findIndex(c => c.section === state.current);
    if (at >= 0) {
      const starts = pageStarts(brew(chapters));
      const source = dom.pages.filter(p => !p.hasAttribute('data-fitted'));
      const text = chapters[at].markdown;
      if (source.length === starts.length) {
        for (const m of text.matchAll(/^\\page(?:break)?(?: *\{[^\n{}]*\})?$/gm)) {
          const index = starts.indexOf(map[at].start + m.index);
          numbers.push(index >= 0 ? dom.numbers[dom.pages.indexOf(source[index])] : null);
        }
      }
    }
  }
  state.breakPages = numbers;
  state.view.dispatch({ effects: setBreaks.of(numbers) });
}

const setBreaks = StateEffect.define();

class PageMarker extends GutterMarker {
  constructor(text) { super(); this.text = text; }
  eq(other) { return other.text === this.text; }
  toDOM() { const el = document.createElement('span'); el.className = 'cm-book-pageno'; el.textContent = this.text; return el; }
}

// The lines that are page breaks, and the number the page that starts there prints ("p. 4").
function pageGutter(state) {
  const breaks = StateField.define({
    create: s => ({ lines: breakLines(s.doc), pages: [] }),
    update(v, tr) {
      let { lines, pages } = v;
      if (tr.docChanged) lines = breakLines(tr.state.doc);
      for (const e of tr.effects) if (e.is(setBreaks)) pages = e.value;
      return lines === v.lines && pages === v.pages ? v : { lines, pages };
    }
  });
  return [breaks, gutter({
    class: 'cm-book-pages',
    lineMarker(view, line) {
      if (state.mode !== 'md') return null;
      const v = view.state.field(breaks);
      const i = v.lines.indexOf(view.state.doc.lineAt(line.from).number);
      const n = i >= 0 ? v.pages[i] : null;
      return n == null ? null : new PageMarker((state.labels.pageNo || 'p. {0}').replace('{0}', String(n)));
    },
    lineMarkerChange: u => u.docChanged || u.transactions.some(t => t.effects.some(e => e.is(setBreaks)))
  })];
}

function breakLines(doc) {
  const lines = [];
  for (let i = 1; i <= doc.lines; i++) if (/^\\page(?:break)?(?: *\{[^\n{}]*\})?$/.test(doc.line(i).text)) lines.push(i);
  return lines;
}

// The preview follows the cursor: to the page holding the nearest heading above it, only when that heading changed.
function followCursor(state) {
  if (!state.follow || state.mode !== 'md' || state.review || !state.dom) return;
  const c = chapter(state);
  const head = state.view.state.selection.main.head;
  const list = outlineFor({ section: state.current, markdown: c.markdown });
  let index = -1;
  for (let i = 0; i < list.length && list[i].offset <= head; i++) index = i;
  const key = state.current + ':' + index + ':' + state.domVersion;
  if (state.aimed === key) return;
  state.aimed = key;
  if (index < 0) scrollTo(state, state.current);
  else scrollPreviewTo(state, headingElement(state, state.current, index));
}

/** The editor's cursor moved to a heading (an outline row, or a click in the preview). `force`: the preview goes there even with following off. */
export function goTo(id, section, index) {
  const state = editors.get(id);
  if (!state || state.mode !== 'md' || state.current !== section) return false;
  const c = chapter(state);
  const h = outlineFor({ section, markdown: c.markdown })[index];
  if (!h) return false;
  const pos = Math.min(h.offset, state.view.state.doc.length);
  state.view.dispatch({ selection: { anchor: pos }, scrollIntoView: true });
  state.view.focus();
  state.aimed = section + ':' + index + ':' + state.domVersion;
  scrollPreviewTo(state, headingElement(state, section, index));
  return true;
}

/** Following on or off (the preview header's toggle). */
export function setFollow(id, on) {
  const state = editors.get(id);
  if (!state) return;
  state.follow = !!on;
  state.aimed = null;
}

/** Fit width or 100%. */
export function setZoom(id, zoom) {
  const state = editors.get(id);
  if (!state) return;
  state.zoom = zoom === '100' ? '100' : 'fit';
  const root = state.frame.contentDocument?.getElementById('brew');
  if (root) fitWidth(state.frame, root, state.zoom);
}

// A click in the pages moves the cursor to the heading above the click; the page under the top of the preview is named in the header.
function listen(state) {
  const doc = state.frame.contentDocument;
  if (!doc) return;
  doc.addEventListener('click', ev => {
    if (!state.follow || state.mode !== 'md' || state.review || !state.dom) return;
    const target = ev.target;
    if (!(target instanceof doc.defaultView.Node)) return;
    let found = -1;
    state.dom.headings.forEach((h, i) => { if (h === target || h.contains(target) || (h.compareDocumentPosition(target) & 4)) found = i; });
    if (found < 0) return;
    let section = null, index = 0;
    for (const [s, base] of state.dom.bases) if (base <= found) { section = s; index = found - base; }
    if (section == null) return;
    pick(state, section, index);
  });
  const status = () => pageStatus(state);
  doc.addEventListener('scroll', status, { passive: true });
}

async function pick(state, section, index) {
  const listLength = outlineFor(state.chapters.find(c => c.section === section) || { section, markdown: '' }).length;
  if (index >= listLength) return;
  if (section !== state.current) await state.dotnet.invokeMethodAsync('PreviewPicked', section);
  if (state.current !== section) return;
  const h = outlineFor({ section, markdown: chapter(state).markdown })[index];
  if (!h) return;
  state.aimed = section + ':' + index + ':' + state.domVersion;
  state.view.dispatch({ selection: { anchor: Math.min(h.offset, state.view.state.doc.length) }, scrollIntoView: true });
  state.view.focus();
}

// "Page 3 of 12" in the preview's header: the page at the top of the frame. Written as text, not announced.
function pageStatus(state) {
  const dom = state.dom;
  const box = state.host.closest('[data-book-editor]');
  const out = box?.querySelector('[data-page-status]');
  if (!out || !dom || !dom.pages.length) return;
  const top = state.frame.contentDocument.scrollingElement?.scrollTop ?? 0;
  const view = state.frame.contentWindow;
  let at = 0;
  dom.pages.forEach((p, i) => { if (p.getBoundingClientRect().top <= (view ? view.innerHeight / 3 : 0) + 1 || top === 0 && i === 0) at = i; });
  out.textContent = (state.labels.pageOf || 'Page {0} of {1}').replace('{0}', String(at + 1)).replace('{1}', String(dom.pages.length));
}

// What a snippet writes. A block wraps the selection (or a line of placeholder text); a break is a line of its own. Written as the draft's own
// Markdown, with no address in it (the renderer's cleaner would drop one).
const BLOCKS = { note: ['{{note', '##### {title}\n', '}}'], aloud: ['{{descriptive', '', '}}'], secret: ['{{secret', '', '}}'], wide: ['{{wide', '', '}}'],
  stat: ['{{monster,frame', '## {name}\n*{type}*\n___\n**{label}** {value}\n', '}}'], picture: ['{{portrait,torn'] };

/**
 * Puts a snippet in at the cursor, or around the selection, as one edit (it saves and undoes like typing). `kind`: note, aloud, secret, stat, table,
 * picture, wide, column, page. False when the editor does not take one (a stylesheet, a locked editor).
 */
export function snippet(id, kind) {
  const state = editors.get(id);
  if (!state || state.mode !== 'md' || state.review || state.view.state.readOnly) return false;
  // The party edition has no secret block; the bar does not offer one, and the editor refuses one.
  if (kind === 'secret' && !state.dm) return false;
  const words = state.labels.snippet || {};
  const fill = t => t.replace(/\{(\w+)\}/g, (_, k) => words[k] ?? k);
  const view = state.view;
  const range = view.state.selection.main;
  const doc = view.state.doc;
  const line = doc.lineAt(range.from);
  const lead = range.from === line.from ? '' : '\n';
  let insert, anchor;
  if (kind === 'page' || kind === 'column') {
    // A break is a line of its own, and never wraps what is selected.
    const at = range.to;
    const after = doc.lineAt(at);
    const text = (at === after.from && after.from === range.from ? '' : '\n') + '\\' + kind + '\n';
    view.dispatch({ changes: { from: at, insert: text }, selection: { anchor: at + text.length }, userEvent: 'input.snippet', scrollIntoView: true });
    view.focus();
    return true;
  }
  if (kind === 'table') {
    insert = lead + fill('| {heading} | {heading} |\n|:--|:--|\n| {cell} | {cell} |\n');
    view.dispatch({ changes: { from: range.from, to: range.to, insert }, selection: { anchor: range.from + insert.length }, userEvent: 'input.snippet', scrollIntoView: true });
    view.focus();
    return true;
  }
  const spec = BLOCKS[kind];
  if (!spec) return false;
  const [open, mid, close] = spec;
  const picked = view.state.sliceDoc(range.from, range.to);
  if (kind === 'picture') {
    // The picture's key is the editor's to suggest (after "art:"), so the slot is chosen from this edition's own.
    const text = lead + open + '\n![' + (picked || fill('{caption}')) + '](art:)\n}}\n';
    const caret = range.from + lead.length + (open + '\n![' + (picked || fill('{caption}')) + '](art:').length;
    view.dispatch({ changes: { from: range.from, to: range.to, insert: text }, selection: { anchor: caret }, userEvent: 'input.snippet', scrollIntoView: true });
    view.focus();
    if (state.assistOn) startCompletion(view);
    return true;
  }
  const body = picked || (kind === 'stat' ? '' : fill('{text}'));
  insert = lead + open + '\n' + fill(mid) + body + '\n' + close + '\n';
  const start = range.from + lead.length + open.length + 1 + fill(mid).length;
  view.dispatch({ changes: { from: range.from, to: range.to, insert }, selection: picked ? { anchor: range.from + insert.length } : { anchor: start, head: start + body.length },
    userEvent: 'input.snippet', scrollIntoView: true });
  view.focus();
  return true;
}

/**
 * The outline tree's keys (docs/BOOK_EXPORT.md 52): Up and Down move through the rows, Home and End to the first and last, Right opens a chapter
 * (or goes into it) and Left closes it (or goes to its chapter); Left and Right swap on a right-to-left page. Enter and Space are the buttons' own.
 * Bound once to each tree on the page, and safe to call after every draw.
 */
export function treeKeys(rtl) {
  for (const tree of document.querySelectorAll('[data-book-tree]')) {
    if (tree.dataset.bound) continue;
    tree.dataset.bound = '1';
    const rows = () => [...tree.querySelectorAll('[role=treeitem]')].filter(r => !r.disabled);
    // Only the row the focus is on is a tab stop.
    tree.addEventListener('focusin', ev => {
      const row = ev.target.closest?.('[role=treeitem]');
      if (row) for (const r of tree.querySelectorAll('[role=treeitem]')) r.tabIndex = r === row ? 0 : -1;
    });
    tree.addEventListener('keydown', ev => {
      const row = ev.target.closest?.('[role=treeitem]');
      if (!row || ev.altKey || ev.ctrlKey || ev.metaKey) return;
      const all = rows();
      const at = all.indexOf(row);
      const level = Number(row.getAttribute('aria-level'));
      const chapterOf = r => r.closest('.book-tree-chapter');
      const toggle = () => chapterOf(row)?.querySelector('.book-tree-toggle');
      const forward = rtl ? 'ArrowLeft' : 'ArrowRight';
      let target = null;
      if (ev.key === 'ArrowDown') target = all[Math.min(all.length - 1, at + 1)];
      else if (ev.key === 'ArrowUp') target = all[Math.max(0, at - 1)];
      else if (ev.key === 'Home') target = all[0];
      else if (ev.key === 'End') target = all[all.length - 1];
      else if (ev.key === 'ArrowRight' || ev.key === 'ArrowLeft') {
        const opens = ev.key === forward;
        if (level === 1 && row.getAttribute('aria-expanded') === 'false' && opens) { toggle()?.click(); }
        else if (level === 1 && row.getAttribute('aria-expanded') === 'true' && !opens) { toggle()?.click(); }
        else if (level === 1 && opens) target = all[at + 1] && Number(all[at + 1].getAttribute('aria-level')) > 1 ? all[at + 1] : null;
        else if (level > 1 && !opens) target = chapterOf(row)?.querySelector('[role=treeitem][aria-level="1"]');
        else return;
      } else return;
      ev.preventDefault();
      target?.focus();
    });
  }
}

/** The editor's selection in the chapter shown: where it starts and ends, and its text (the server checks it is what it stores there). Null
 * for the stylesheet. Nothing selected: from equals to, and the server takes the whole chapter. */
export function selection(id) {
  const state = editors.get(id);
  if (!state || state.mode !== 'md') return null;
  const range = state.view.state.selection.main;
  return { section: state.current, from: range.from, to: range.to, text: state.view.state.sliceDoc(range.from, range.to) };
}

/** Typing on or off: off while an AI edit is asked for and while it is reviewed, so what the model was sent is still what is there. */
export function lock(id, on) {
  const state = editors.get(id);
  if (!state) return;
  state.lockedOn = !!on;
  state.view.dispatch({ effects: [state.locked.reconfigure(on ? [EditorState.readOnly.of(true), EditorView.editable.of(false)] : []), setChips.of(null)] });
}

/**
 * Shows an AI edit for review (docs/BOOK_EXPORT.md 41.4): the passage in a read-only view of its own, each change with Accept and Reject, deleted
 * text struck through and new text marked, word by word inside a changed line. The chapter's editor is locked and hidden meanwhile, and the
 * preview draws the chapter with only the changes accepted so far. Nothing is saved until finish(). False (and nothing shown) when the
 * chapter's text there is no longer what the model was sent.
 */
export function review(id, proposal, labels) {
  const state = editors.get(id);
  if (!state || state.mode !== 'md' || state.review || state.current !== proposal.section) return false;
  const doc = state.view.state.doc;
  if (proposal.to > doc.length || doc.sliceString(proposal.from, proposal.to) !== proposal.original) return false;
  lock(id, true);
  const holder = state.host.ownerDocument.createElement('div');
  holder.className = 'book-draft-review-code';
  holder.dataset.testid = 'book-draft-review-code';
  state.host.after(holder);
  state.host.hidden = true;
  const words = labels || {};
  // The count of changes left goes in the review bar, which the server draws just after this returns: until it is there, try again shortly.
  const count = (tries = 40) => {
    const left = state.host.closest('[data-book-editor]')?.querySelector('[data-review-left]');
    if (!left) { if (tries > 0 && state.review?.view === view) setTimeout(() => count(tries - 1), 50); return; }
    const n = view ? getChunks(view.state)?.chunks.length ?? 0 : 0;
    left.textContent = (words.left || '{0}').replace('{0}', String(n));
  };
  const repaint = debounce(() => preview(id), 250);
  let view = null;
  view = new EditorView({
    parent: holder,
    state: EditorState.create({
      doc: proposal.replacement,
      extensions: [basicSetup, bookHighlight, EditorView.lineWrapping, state.direction, markdown(), EditorState.readOnly.of(true),
        EditorState.phrases.of({ Accept: words.accept || 'Accept', Reject: words.reject || 'Reject' }),
        unifiedMergeView({ original: proposal.original, highlightChanges: true, gutter: true, mergeControls: true, syntaxHighlightDeletions: false,
          diffConfig: reviewDiffConfig }),
        // From the keyboard: Alt-Down and Alt-Up go from change to change, Ctrl-Enter accepts the one at the cursor, Ctrl-Backspace rejects it.
        keymap.of([
          { key: 'Alt-ArrowDown', run: goToNextChunk },
          { key: 'Alt-ArrowUp', run: goToPreviousChunk },
          { key: 'Mod-Enter', run: v => acceptChunk(v) },
          { key: 'Mod-Backspace', run: v => rejectChunk(v) }
        ]),
        EditorView.contentAttributes.of({ 'aria-label': words.label || '' }),
        EditorView.updateListener.of(u => { if (u.docChanged || u.transactions.some(t => t.effects.length)) { count(); repaint(); } })]
    })
  });
  state.review = { section: proposal.section, from: proposal.from, to: proposal.to, original: proposal.original, view, holder };
  count();
  view.focus();
  preview(id);
  return true;
}

/**
 * Ends a review. `how` is 'accepted' (only the changes accepted), 'all' (every change not rejected), or 'none'. What is kept goes into the
 * chapter as one change, as if typed: it is saved like typing, and Ctrl-Z in the editor takes it back.
 */
export function finish(id, how) {
  const state = editors.get(id);
  const r = state?.review;
  if (!r) return false;
  const text = how === 'all' ? r.view.state.doc.toString() : how === 'accepted' ? getOriginalDoc(r.view.state).toString() : r.original;
  r.view.destroy();
  r.holder.remove();
  state.review = null;
  state.host.hidden = false;
  lock(id, false);
  if (text !== r.original && state.mode === 'md' && state.current === r.section) {
    state.view.dispatch({ changes: { from: r.from, to: r.to, insert: text }, selection: { anchor: r.from + text.length }, scrollIntoView: true, userEvent: 'input.ai' });
  } else {
    preview(id);
  }
  state.view.focus();
  return text !== r.original;
}

/**
 * Ends a review without writing into the chapter, and returns the text kept ('accepted' or 'all'), or null for 'none'. For "Bring up to date"
 * (docs/BOOK_EXPORT.md 41.18): the server saves the kept text together with the chapter's new base, and the page then puts it in the editor,
 * so it is not in the editor's undo history (Ctrl-Z there would bring back the old text over the new base, and the notice would not come back).
 * The chapter stays locked until the page unlocks it.
 */
export function take(id, how) {
  const state = editors.get(id);
  const r = state?.review;
  if (!r) return null;
  const text = how === 'all' ? r.view.state.doc.toString() : how === 'accepted' ? getOriginalDoc(r.view.state).toString() : null;
  r.view.destroy();
  r.holder.remove();
  state.review = null;
  state.host.hidden = false;
  preview(id);
  return text;
}

export function close(id) {
  const state = editors.get(id);
  if (!state) return;
  if (state.review) { state.review.view.destroy(); state.review.holder.remove(); state.review = null; }
  state.changed.cancel();
  state.view.destroy();
  editors.delete(id);
}
