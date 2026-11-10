// Pictures in the text (docs/BOOK_EXPORT.md 53): under an entry's heading whose place has no picture, a chip to upload one, ask for a
// suggestion, or draw the suggested one; and after "art:KEY", the name of the place it points at. A chip is a widget beside the text, never
// text: nothing here changes the chapter except "put it in the text" and a picture that was made, each one edit that undoes like typing.
// The chip shows what the server said and asks the server to act; the server checks every request again (the slot, its edition, its
// suggestion, the description), so nothing here is trusted.
import { EditorView, Decoration, WidgetType, keymap } from '@codemirror/view';
import { StateField, StateEffect, Prec } from '@codemirror/state';

const MARK = /^\{\{entry,#e-(s[0-9a-f]{24})\}\}\s*$/;
const FENCE = /^\s*(`{3,}|~{3,})/;
const ART = /art:(s[0-9a-f]{24})/g;

/**
 * Where each entry of a chapter stands: [{key, from, at}], `from` the start of its marker line and `at` the end of its heading line (the
 * marker's own line when no heading follows it). A marker inside a code fence is text, not an entry.
 */
export function entryPlaces(markdown) {
  const out = [];
  let fence = null;
  let offset = 0;
  const lines = markdown.split('\n');
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const f = FENCE.exec(line);
    if (f) {
      if (fence == null) fence = f[1][0];
      else if (f[1][0] === fence) fence = null;
    } else if (fence == null) {
      const m = MARK.exec(line);
      if (m) {
        const next = lines[i + 1];
        const heading = next != null && /^#{1,6}\s/.test(next);
        out.push({ key: m[1], from: offset, at: offset + line.length + (heading ? 1 + next.length : 0) });
      }
    }
    offset += line.length + 1;
  }
  return out;
}

/** True when the text already points at the place's picture. */
export function referenced(markdown, key) {
  return markdown.includes('art:' + key);
}

/**
 * The edit that puts a place's picture block under its entry's heading: { at, insert }, or null when the chapter has no such entry or already
 * points at the picture. The block stands apart by a blank line on each side, as the generator writes it.
 */
export function placeEdit(markdown, key, block) {
  if (referenced(markdown, key)) return null;
  const place = entryPlaces(markdown).find(p => p.key === key);
  if (!place) return null;
  const after = markdown.slice(place.at, place.at + 2);
  return { at: place.at, insert: '\n\n' + block + (after === '\n\n' ? '' : '\n') };
}

/**
 * What a chip shows: null (no chip), 'ready' (a picture is there, the text does not point at it), 'drawing', 'suggesting', 'stale', 'idea',
 * 'empty'. `data.busy` = { kind: 'draw', keys } (the places a running drawing still has to draw) or { kind: 'suggest', sections } (the
 * chapters the art director is being asked about).
 */
export function chipState(chip, inText, data) {
  if (chip.pictured) return inText ? null : 'ready';
  const busy = data?.busy;
  if (busy?.kind === 'draw' && busy.keys?.includes(chip.key)) return 'drawing';
  if (busy?.kind === 'suggest' && busy.sections?.includes(chip.section) && (!chip.idea || chip.idea.stale)) return 'suggesting';
  if (chip.idea) return chip.idea.stale ? 'stale' : 'idea';
  return 'empty';
}

/** A suggestion's words as the chip shows them: cut at 60 characters, on a word where one ends nearby. */
export function cut(text, max = 60) {
  const t = String(text ?? '').replace(/\s+/g, ' ').trim();
  if (t.length <= max) return t;
  const head = t.slice(0, max);
  const space = head.lastIndexOf(' ');
  return (space > max * 0.6 ? head.slice(0, space) : head).replace(/[\s,;:.]+$/, '') + '…';
}

/** The "art:KEY" references of a text that name a known place: [{ at, label }], `at` after the key and the bracket that closes it. */
export function artHints(markdown, names) {
  const out = [];
  for (const m of markdown.matchAll(ART)) {
    const label = names.get(m[1]);
    const end = m.index + m[0].length;
    if (label) out.push({ at: end + (markdown[end] === ')' ? 1 : 0), label });
  }
  return out;
}

export const setChips = StateEffect.define();

let tips = 0;

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

function button(text, cls, run) {
  const b = el('button', 'cm-book-chip-btn' + (cls ? ' ' + cls : ''), text);
  b.type = 'button';
  // Not a tab stop: a chapter can hold dozens of chips. Alt-Enter in the text goes to the chip of the entry the cursor is in.
  b.tabIndex = -1;
  b.addEventListener('click', ev => {
    ev.preventDefault();
    if (b.getAttribute('aria-disabled') === 'true' || b.disabled) return;
    run(b);
  });
  return b;
}

class HintWidget extends WidgetType {
  constructor(label) { super(); this.label = label; }
  eq(other) { return other.label === this.label; }
  toDOM() {
    const s = el('span', 'cm-book-arthint', this.label);
    s.setAttribute('dir', 'auto');
    return s;
  }
  ignoreEvent() { return false; }
}

class ChipWidget extends WidgetType {
  constructor(chip, kind, data, ctx) {
    super();
    this.chip = chip;
    this.kind = kind;
    this.data = data;
    this.ctx = ctx;
    this.sig = JSON.stringify([chip, kind, data.model, data.price, data.drawAsk, data.locked]);
  }

  eq(other) { return other.sig === this.sig; }

  get estimatedHeight() { return 30; }

  ignoreEvent() { return true; }

  toDOM(view) {
    const { chip, kind, data, ctx } = this;
    const L = ctx.labels();
    const say = (key, ...args) => args.reduce((t, a, i) => t.replace('{' + i + '}', a), L[key] ?? key);
    // The chip, and under it (not inside it, so it can be as wide as the text) what shows on hover or focus.
    const wrap = el('div', 'cm-book-chip-wrap');
    const box = el('div', 'cm-book-chip');
    wrap.append(box);
    box.dataset.chip = chip.key;
    box.dataset.state = kind;
    box.setAttribute('role', 'group');
    box.setAttribute('aria-label', say('group', chip.label));
    box.setAttribute('aria-keyshortcuts', 'Alt+Enter');
    if (!data.model && (kind === 'idea' || kind === 'stale')) box.classList.add('off');
    box.append(el('span', 'cm-book-chip-icon'));
    const row = el('span', 'cm-book-chip-row');
    box.append(row);

    const act = (what) => ctx.act(what, chip.key);
    const lock = b => { if (data.locked) b.disabled = true; return b; };
    const focusFirst = () => row.querySelector('button:not([disabled]), a')?.focus();

    // A question asked in the chip itself (the cost, then yes or no): the buttons give way to it and come back on Cancel.
    const ask = (text, yes, run, back) => {
      const q = el('span', 'cm-book-chip-ask');
      const words = el('span', null, text);
      words.setAttribute('role', 'status');
      q.append(words);
      if (yes) q.append(button(yes, 'primary', () => { q.remove(); row.hidden = false; run(); }));
      q.append(button(say('cancel'), null, () => { q.remove(); row.hidden = false; back?.focus(); }));
      row.hidden = true;
      box.append(q);
      q.querySelector('button')?.focus();
      return q;
    };

    const upload = () => lock(button(say('upload'), null, () => ctx.upload(chip.key)));
    const suggest = label => lock(button(label, null, async b => {
      const waiting = ask(say('asking'), null, null, b);
      let quote = null;
      try { quote = await ctx.quote(chip.key); } catch { quote = null; }
      if (!waiting.isConnected) return;
      waiting.remove();
      row.hidden = false;
      if (!quote) { b.focus(); return; }
      ask(quote.text, quote.ok ? say('suggestYes') : null, () => act('suggest'), b);
    }));

    if (kind === 'ready') {
      row.append(el('span', 'cm-book-chip-text', say('ready')), lock(button(say('place'), 'primary', () => ctx.place(chip))));
    } else if (kind === 'drawing' || kind === 'suggesting') {
      const status = el('span', 'cm-book-chip-text', say(kind));
      status.setAttribute('role', 'status');
      row.append(el('span', 'cm-book-chip-spin'), status, button(say('stop'), null, () => act('stop')));
    } else if (kind === 'empty') {
      // "Add picture" opens a short menu: upload one, or ask the art director about this chapter's places.
      const menu = el('span', 'cm-book-chip-menu');
      menu.setAttribute('role', 'menu');
      menu.hidden = true;
      const opener = lock(button(say('add'), null, b => {
        menu.hidden = !menu.hidden;
        b.setAttribute('aria-expanded', menu.hidden ? 'false' : 'true');
        if (!menu.hidden) menu.querySelector('button:not([disabled])')?.focus();
      }));
      opener.setAttribute('aria-haspopup', 'menu');
      opener.setAttribute('aria-expanded', 'false');
      const items = [upload(), suggest(say('suggest'))];
      for (const item of items) item.setAttribute('role', 'menuitem');
      menu.append(...items);
      row.append(opener, menu);
    } else {
      // A suggestion: what it shows, and on focus or hover exactly what a picture model would be sent, with the price.
      const idea = chip.idea;
      const tip = el('div', 'cm-book-chip-tip');
      tip.id = 'cm-book-chip-tip-' + (++tips);
      tip.setAttribute('role', 'tooltip');
      const words = el('span', 'cm-book-chip-text', say('picture', cut(idea.depicts)));
      words.setAttribute('dir', 'auto');
      row.append(words);
      const drawable = kind === 'idea' && !!idea.prompt && data.model;
      if (kind === 'stale') {
        tip.append(el('div', null, say('stale')));
      } else if (!idea.prompt) {
        tip.append(el('div', null, say('problem', (idea.problems || []).join(' '))));
      } else {
        tip.append(el('div', 'cm-book-chip-tip-head', say('prompt')));
        const prompt = el('div', 'cm-book-chip-prompt', idea.prompt);
        // The prompt is written in English whatever the book's language.
        prompt.setAttribute('dir', 'ltr');
        prompt.setAttribute('lang', 'en');
        tip.append(prompt);
      }
      if (!data.model) {
        const none = el('div', 'cm-book-chip-nomodel', say('noModel') + ' ');
        const link = el('a', null, say('setUp'));
        link.href = '/settings/ai';
        link.tabIndex = -1;
        none.append(link);
        tip.append(none);
      } else if (data.price) {
        tip.append(el('div', 'cm-book-chip-price', data.price));
      }
      const draw = button(say('draw'), 'primary', b => ask(data.drawAsk || say('draw'), say('drawYes'), () => act('draw'), b));
      if (!drawable) draw.setAttribute('aria-disabled', 'true');
      draw.setAttribute('aria-describedby', tip.id);
      lock(draw);
      row.append(draw);
      if (kind === 'stale') row.append(suggest(say('suggestAgain')));
      row.append(upload(), lock(button(say('dismiss'), null, () => act('dismiss'))));
      if (kind === 'stale') row.append(el('span', 'cm-book-chip-note', say('stale')));
      wrap.append(tip);
    }

    // Left and Right move between the chip's buttons (swapped on a right-to-left line); Escape closes a question, then goes back to the text.
    wrap.addEventListener('keydown', ev => {
      if (ev.key === 'Escape') {
        ev.preventDefault();
        const q = box.querySelector('.cm-book-chip-ask');
        if (q) { q.remove(); row.hidden = false; focusFirst(); } else view.focus();
        return;
      }
      if (ev.key !== 'ArrowLeft' && ev.key !== 'ArrowRight') return;
      const all = [...wrap.querySelectorAll('button, a')].filter(b => !b.disabled && b.offsetParent !== null);
      const at = all.indexOf(document.activeElement);
      if (at < 0) return;
      ev.preventDefault();
      const rtl = getComputedStyle(box).direction === 'rtl';
      const step = (ev.key === 'ArrowRight') !== rtl ? 1 : -1;
      all[(at + step + all.length) % all.length].focus();
    });
    return wrap;
  }
}

function build(doc, data, ctx) {
  const text = doc.toString();
  const list = [];
  if (data?.chips?.length) {
    const byKey = new Map(data.chips.map(c => [c.key, c]));
    for (const place of entryPlaces(text)) {
      const chip = byKey.get(place.key);
      if (!chip) continue;
      const kind = chipState(chip, referenced(text, chip.key), data);
      if (!kind) continue;
      list.push(Decoration.widget({ widget: new ChipWidget(chip, kind, data, ctx), block: true, side: 1 }).range(place.at));
    }
  }
  for (const hint of artHints(text, ctx.names)) list.push(Decoration.widget({ widget: new HintWidget(hint.label), side: 1 }).range(hint.at));
  return Decoration.set(list, true);
}

/**
 * The editor's extensions. `ctx` = { data(): what the server last said, labels(): the chip's words, names: Map of slot key to its label,
 * act(kind, key), quote(key) -> {text, ok}, upload(key), place(chip) }.
 */
export function chips(ctx) {
  const field = StateField.define({
    create: s => build(s.doc, ctx.data(), ctx),
    update(value, tr) {
      const told = tr.effects.some(e => e.is(setChips));
      return tr.docChanged || told ? build(tr.state.doc, ctx.data(), ctx) : value;
    },
    provide: f => EditorView.decorations.from(f)
  });

  // Alt-Enter: to the chip of the entry the cursor is in, wherever it has scrolled to.
  const toChip = view => {
    const head = view.state.selection.main.head;
    const places = entryPlaces(view.state.doc.toString()).filter(p => p.from <= head);
    const place = places[places.length - 1];
    if (!place) return false;
    const find = () => view.dom.querySelector(`[data-chip="${place.key}"]`);
    const focus = () => { const b = find()?.querySelector('button:not([disabled]), a'); if (b) b.focus(); return !!b; };
    if (focus()) return true;
    view.dispatch({ effects: EditorView.scrollIntoView(place.at, { y: 'center' }) });
    requestAnimationFrame(() => focus());
    return true;
  };

  return [field, Prec.high(keymap.of([{ key: 'Alt-Enter', run: toChip }])), chipTheme];
}

const chipTheme = EditorView.baseTheme({
  // Padding, never a margin: the editor measures a block widget's own box, and a margin outside it puts every click below it on the wrong line.
  '.cm-book-chip-wrap': { position: 'relative', padding: '2px 0 4px' },
  '.cm-book-chip': { display: 'flex', alignItems: 'center', gap: '.4rem', flexWrap: 'wrap', margin: '0', padding: '2px 6px',
    font: '500 .78rem/1.5 var(--font-body, system-ui, sans-serif)', color: 'var(--text-2)', border: '1px dashed var(--border-strong, var(--border))', borderRadius: '4px',
    background: 'var(--surface-2, transparent)', width: 'fit-content', maxWidth: '100%' },
  '.cm-book-chip.off .cm-book-chip-row, .cm-book-chip.off .cm-book-chip-icon': { opacity: '.65' },
  '.cm-book-chip-icon': { flex: 'none', width: '.85rem', height: '.7rem', border: '1.5px dashed currentColor', borderRadius: '2px' },
  '.cm-book-chip-row, .cm-book-chip-ask, .cm-book-chip-menu': { display: 'inline-flex', alignItems: 'center', gap: '.4rem', flexWrap: 'wrap' },
  '.cm-book-chip-row[hidden], .cm-book-chip-menu[hidden]': { display: 'none' },
  '.cm-book-chip-text': { color: 'var(--text)' },
  '.cm-book-chip-note': { color: 'var(--text-3)' },
  '.cm-book-chip-btn': { font: 'inherit', padding: '0 .45rem', border: '1px solid var(--border)', borderRadius: '3px', background: 'var(--surface)', color: 'var(--text)', cursor: 'pointer' },
  '.cm-book-chip-btn.primary': { borderColor: 'var(--accent)', color: 'var(--accent)' },
  '.cm-book-chip-btn:hover, .cm-book-chip-btn:focus-visible': { background: 'var(--surface-3)' },
  '.cm-book-chip-btn:focus-visible': { outline: '2px solid var(--accent)', outlineOffset: '1px' },
  '.cm-book-chip-btn:disabled, .cm-book-chip-btn[aria-disabled="true"]': { opacity: '.5', cursor: 'not-allowed' },
  '.cm-book-chip-spin': { width: '.75rem', height: '.75rem', border: '2px solid var(--border)', borderTopColor: 'var(--accent)', borderRadius: '50%', animation: 'cm-book-chip-spin 1s linear infinite' },
  '@keyframes cm-book-chip-spin': { to: { transform: 'rotate(360deg)' } },
  '@media (prefers-reduced-motion: reduce)': { '.cm-book-chip-spin': { animation: 'none' } },
  '.cm-book-chip-tip': { display: 'none', position: 'absolute', zIndex: '5', insetInlineStart: '0', top: 'calc(100% - 6px)', width: 'max-content', maxWidth: 'min(30rem, 100%)', boxSizing: 'border-box',
    padding: '6px 8px', border: '1px solid var(--border)', borderRadius: '4px', background: 'var(--surface)', color: 'var(--text)', boxShadow: 'var(--shadow-2, 0 2px 8px rgba(0,0,0,.25))',
    fontWeight: '400', whiteSpace: 'normal' },
  // Shown while the pointer or the focus is on the chip, and while it is on the tip itself (it can hold a link).
  '.cm-book-chip:hover + .cm-book-chip-tip, .cm-book-chip:focus-within + .cm-book-chip-tip, .cm-book-chip-tip:hover, .cm-book-chip-tip:focus-within': { display: 'block' },
  '.cm-book-chip-tip-head, .cm-book-chip-price, .cm-book-chip-nomodel': { color: 'var(--text-2)' },
  '.cm-book-chip-prompt': { margin: '2px 0', fontFamily: 'var(--font-mono, monospace)', fontSize: '.74rem', textAlign: 'left' },
  '.cm-book-arthint': { marginInlineStart: '.35rem', padding: '0 .3rem', fontSize: '.78em', color: 'var(--text-3)', border: '1px solid var(--border)', borderRadius: '3px',
    fontFamily: 'var(--font-body, system-ui, sans-serif)', unicodeBidi: 'isolate' }
});
