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
import { brew, layout } from './render.js';

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
  const direction = book.rtl ? EditorView.contentAttributes.of({ dir: 'rtl' }) : [];
  const changed = debounce(() => {
    if (state.mode === 'css') dotnet.invokeMethodAsync('CssChanged');
    else dotnet.invokeMethodAsync('Changed', state.current);
  }, 900);
  const repaint = debounce(() => preview(id), 350);
  state.changed = changed;
  state.view = new EditorView({
    parent: host,
    state: EditorState.create({
      doc: text(state),
      extensions: [basicSetup, keymap.of([indentWithTab]), EditorView.lineWrapping, direction, language.of(markdown()),
        EditorView.updateListener.of(u => {
          if (!u.docChanged) return;
          const value = u.state.doc.toString();
          if (state.mode === 'css') state.css = value; else chapter(state).markdown = value;
          changed();
          repaint();
        })]
    })
  });
  state.language = language;

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

function chapter(state) {
  return state.chapters.find(c => c.section === state.current) || { markdown: '' };
}

function text(state) {
  return state.mode === 'css' ? state.css : chapter(state).markdown;
}

function load(state, value) {
  state.view.dispatch({ changes: { from: 0, to: state.view.state.doc.length, insert: value } });
}

/** Saves whatever is waiting at once (before the dialog closes or another chapter is shown). */
export function flush(id) {
  const state = editors.get(id);
  if (state) state.changed.now();
}

/** Shows another chapter, or the stylesheet (`section` null). */
export function show(id, section) {
  const state = editors.get(id);
  if (!state) return;
  state.changed.now();
  state.mode = section == null ? 'css' : 'md';
  if (section != null) state.current = section;
  // Swapping the text must not count as typing it.
  const listener = state.changed;
  state.changed = Object.assign(() => {}, { now() {}, cancel() {} });
  state.view.dispatch({ effects: state.language.reconfigure(state.mode === 'css' ? css() : markdown()) });
  load(state, text(state));
  state.changed = listener;
  state.changed.cancel();
  state.view.focus();
  if (section != null) scrollTo(state, section);
}

/** The server replaced a chapter (Regenerate, undo, another tab): the editor and the preview take its text. */
export async function replace(id, section, markdown) {
  const state = editors.get(id);
  if (!state) return;
  const c = state.chapters.find(x => x.section === section);
  if (c) c.markdown = markdown; else state.chapters.push({ section, markdown });
  if (state.mode === 'md' && state.current === section) {
    const listener = state.changed;
    state.changed = Object.assign(() => {}, { now() {}, cancel() {} });
    load(state, markdown);
    state.changed = listener;
    state.changed.cancel();
  }
  await preview(id);
}

/** The chapters changed as a set (a chapter added or removed): order and text. */
export async function chapters(id, list) {
  const state = editors.get(id);
  if (!state) return;
  state.chapters = list.map(c => ({ ...c }));
  await preview(id);
}

/** A chapter's text (or the stylesheet's, `section` null), for the server to read as a stream. */
export function read(id, section) {
  const state = editors.get(id);
  if (!state) return new Uint8Array();
  const value = section == null ? state.css : (state.chapters.find(c => c.section === section) || { markdown: '' }).markdown;
  return new TextEncoder().encode(value);
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
    const result = await layout(doc, root, brew(state.chapters), state.art);
    if (scroller) scroller.scrollTop = top;
    const status = state.host.closest('[data-book-editor]')?.querySelector('[data-overflow]');
    if (status) {
      status.textContent = result.overflow.length ? (state.labels.overflow || '').replace('{0}', result.overflow.join(', ')) : '';
      status.hidden = result.overflow.length === 0;
    }
  } finally {
    state.rendering = false;
    if (state.again) { state.again = false; preview(id); }
  }
}

function scrollTo(state, section) {
  const doc = state.frame.contentDocument;
  const target = doc && doc.getElementById('ch-' + section);
  if (target) target.closest('.page')?.scrollIntoView({ block: 'start' });
  else if (doc && doc.scrollingElement && section === 'front') doc.scrollingElement.scrollTop = 0;
}

export function close(id) {
  const state = editors.get(id);
  if (!state) return;
  state.changed.cancel();
  state.view.destroy();
  editors.delete(id);
}
