// The book editor's browser half (docs/BOOK_EXPORT.md 41): CodeMirror for a chapter's Markdown and for the GM's stylesheet, and the live
// preview of the whole book in a sandboxed frame. The server owns the draft: this module holds the chapters' text while the dialog is open,
// tells the server when a chapter changed (a small call), and hands the text over as a stream when the server asks for it, so a long
// chapter never has to fit in one message.
import { EditorView, basicSetup } from 'codemirror';
import { EditorState, Compartment } from '@codemirror/state';
import { keymap } from '@codemirror/view';
import { indentWithTab } from '@codemirror/commands';
import { markdown } from '@codemirror/lang-markdown';
import { css } from '@codemirror/lang-css';
import { HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { tags } from '@lezer/highlight';
import { unifiedMergeView, getOriginalDoc, getChunks, acceptChunk, rejectChunk, goToNextChunk, goToPreviousChunk } from '@codemirror/merge';
import { layout } from './render.js';

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
 * Opens the editor in `host`, the preview in `frame`. `book` = { chapters: [{section, markdown}], css, shell, artUrl, current, rtl }.
 * `dotnet` gets Changed(section) after typing stops, and CssChanged() for the stylesheet.
 */
export async function open(host, frame, book, dotnet, labels) {
  const id = 'e' + (++counter);
  const state = { host, frame, dotnet, chapters: book.chapters.map(c => ({ ...c })), css: book.css || '', current: book.current, mode: 'md',
    art: {}, rendering: false, again: false, labels: labels || {} };
  editors.set(id, state);

  const language = new Compartment();
  const locked = new Compartment();
  const direction = book.rtl ? EditorView.contentAttributes.of({ dir: 'rtl' }) : [];
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
  new ResizeObserver(() => { const r = frame.contentDocument?.getElementById('brew'); if (r) fitWidth(frame, r); }).observe(frame);
  state.changed = changed;
  state.view = new EditorView({
    parent: host,
    state: EditorState.create({
      doc: text(state),
      extensions: [basicSetup, bookHighlight, keymap.of([indentWithTab]), EditorView.lineWrapping, direction, language.of(markdown()), locked.of([]),
        EditorView.updateListener.of(u => {
          // A chapter put into the editor (shown, replaced by the server) is not typing: it must not be saved back.
          if (!u.docChanged || state.loading) return;
          const value = u.state.doc.toString();
          if (state.mode === 'css') { state.css = value; state.cssPending = true; } else { chapter(state).markdown = value; state.pending.add(state.current); }
          changed();
          repaint();
        })]
    })
  });
  state.language = language;
  state.locked = locked;
  state.direction = direction;

  // The frame is sandboxed with no script; allow-same-origin only so this page can draw into it and measure what it drew.
  frame.srcdoc = book.shell;
  await new Promise(r => frame.addEventListener('load', r, { once: true }));
  try {
    const answer = await fetch(book.artUrl, { credentials: 'same-origin', cache: 'no-store' });
    if (answer.ok) state.art = await answer.json();
  } catch {
    // No pictures in the preview; the book still draws, with empty boxes where they go.
  }
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
  return state.mode === 'css' ? state.css : chapter(state).markdown;
}

function load(state, value) {
  state.loading = true;
  try {
    state.view.dispatch({ changes: { from: 0, to: state.view.state.doc.length, insert: value } });
  } finally {
    state.loading = false;
  }
}

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

/** Shows another chapter, or the stylesheet (`section` null). */
export function show(id, section) {
  const state = editors.get(id);
  if (!state) return;
  state.mode = section == null ? 'css' : 'md';
  if (section != null) state.current = section;
  state.view.dispatch({ effects: state.language.reconfigure(state.mode === 'css' ? css() : markdown()) });
  load(state, text(state));
  state.view.focus();
  if (section != null) scrollTo(state, section);
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
  await preview(id);
}

/** The chapters changed as a set (a chapter added or removed): order and text. */
export async function chapters(id, list) {
  const state = editors.get(id);
  if (!state) return;
  state.chapters = list.map(c => ({ ...c }));
  await preview(id);
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
    fitWidth(state.frame, root);
    if (scroller) scroller.scrollTop = top;
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
function fitWidth(frame, root) {
  const page = root.querySelector('.page');
  if (!page) return;
  const scale = Math.min(1, (frame.clientWidth - 24) / page.offsetWidth);
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
  state.view.dispatch({ effects: state.locked.reconfigure(on ? [EditorState.readOnly.of(true), EditorView.editable.of(false)] : []) });
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
        unifiedMergeView({ original: proposal.original, highlightChanges: true, gutter: true, mergeControls: true, syntaxHighlightDeletions: false }),
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

export function close(id) {
  const state = editors.get(id);
  if (!state) return;
  if (state.review) { state.review.view.destroy(); state.review.holder.remove(); state.review = null; }
  state.changed.cancel();
  state.view.destroy();
  editors.delete(id);
}
