// Suggestions and help while writing a chapter (docs/BOOK_EXPORT.md 47): what can follow "{{" (the blocks every theme styles), a picture block's
// frames, "\page" and "\column", and after "art:" this edition's own picture slots by their labels; hovering a block's name, a picture's key or an
// entry's marker says what it is. Nothing here changes the text by itself: a suggestion is only typed when it is picked. The GM can turn it all
// off; the editor then is a plain text box again.
import { autocompletion, completionKeymap } from '@codemirror/autocomplete';
import { EditorState, Prec } from '@codemirror/state';
import { hoverTooltip, keymap } from '@codemirror/view';

// The blocks a GM writes. The generator's own (chapter, entry, cover-art, chapter-art, properties) are not offered: they are the program's.
const BLOCKS = ['note', 'descriptive', 'secret', 'monster', 'portrait', 'place', 'spot', 'wide', 'quote'];
const PICTURES = new Set(['portrait', 'place', 'spot', 'chapter-art', 'cover-art']);
const FRAMES = ['torn', 'oval', 'round'];
const BREAKS = ['page', 'column'];

/**
 * The extensions for one editor. `help` = { blocks: {name: text}, frames: {name: text}, breaks: {name: text}, slot, entry, spot, gmOnly },
 * all in the GM's language; `slots` = [{key, kind, label}] of this edition. `dm`: the GM edition (only it may hold a secret block).
 */
export function assist(help, slots, dm) {
  const byKey = new Map(slots.map(s => [s.key, s]));
  const name = s => s.kind === 'spot' && help.spot ? help.spot.replace('{0}', s.label) : s.label;
  const complete = completions(help, slots, dm);

  // What is under the pointer: a block's name, a picture's slot key, an entry's marker.
  const hover = hoverTooltip((view, pos) => {
    const line = view.state.doc.lineAt(pos);
    const at = pos - line.from;
    const tests = [
      { re: /\{\{entry,#e-([A-Za-z0-9]+)\}\}/g, say: k => byKey.has(k) && help.entry ? help.entry.replace('{0}', name(byKey.get(k))) : null },
      { re: /art:([A-Za-z0-9]+)/g, say: k => byKey.has(k) ? (help.slot || '{0}').replace('{0}', name(byKey.get(k))) : null },
      { re: /^\s*\{\{([A-Za-z-]+)/g, say: k => (k === 'secret' && !dm ? help.gmOnly : null) || help.blocks?.[k] || null }
    ];
    for (const { re, say } of tests) {
      for (const match of line.text.matchAll(re)) {
        if (at < match.index || at > match.index + match[0].length) continue;
        const text = say(match[1]);
        if (!text) continue;
        return {
          pos: line.from + match.index, end: line.from + match.index + match[0].length, above: true,
          create() {
            const dom = document.createElement('div');
            dom.className = 'cm-book-help';
            dom.textContent = text;
            return { dom };
          }
        };
      }
    }

    return null;
  });

  return [
    EditorState.languageData.of(() => [{ autocomplete: complete }]),
    autocompletion({ activateOnTyping: true, icons: false }),
    Prec.high(keymap.of(completionKeymap)),
    hover
  ];
}

// A picture's key, and its closing bracket unless one is there already (the editor closes a bracket as it is opened).
function pick(key) {
  return (view, completion, from, to) => {
    const closed = view.state.sliceDoc(to, to + 1) === ')';
    view.dispatch({ changes: { from, to, insert: key + (closed ? '' : ')') }, selection: { anchor: from + key.length + 1 } });
  };
}

/** The completion source alone (for its tests): what may be typed at the cursor, or null. */
export function completions(help, slots, dm) {
  const name = s => s.kind === 'spot' && help.spot ? help.spot.replace('{0}', s.label) : s.label;
  return context => {
    const line = context.state.doc.lineAt(context.pos);
    const before = line.text.slice(0, context.pos - line.from);

    // A picture's slot: "](art:" and what is typed since.
    let m = /\]\(art:([^\s)]*)$/.exec(before);
    if (m) {
      return {
        from: context.pos - m[1].length,
        // Found by the label the GM knows ("Ve" finds Vera); the key is what is typed.
        options: slots.map(s => ({ label: name(s), detail: s.key, type: 'variable', apply: pick(s.key) })),
        filter: true
      };
    }

    // A block: "{{" at the start of a line, then its name, then classes after commas.
    m = /^\s*\{\{([A-Za-z-]*(?:,[A-Za-z#-]*)*)$/.exec(before);
    if (m) {
      const parts = m[1].split(',');
      const typed = parts[parts.length - 1];
      const from = context.pos - typed.length;
      if (parts.length === 1) {
        const blocks = BLOCKS.filter(b => dm || b !== 'secret');
        return { from, options: blocks.map(b => ({ label: b, info: help.blocks?.[b], type: 'keyword', apply: b })), validFor: /^[A-Za-z-]*$/ };
      }

      const block = parts[0];
      const options = PICTURES.has(block) ? FRAMES.map(f => ({ label: f, info: help.frames?.[f], type: 'property' }))
        : block === 'monster' ? [{ label: 'frame', info: help.frames?.frame, type: 'property' }]
        : [];
      return options.length ? { from, options: options.filter(o => !parts.slice(1, -1).includes(o.label)), validFor: /^[A-Za-z-]*$/ } : null;
    }

    // A break: a backslash alone at the start of a line.
    m = /^\s*\\([a-z]*)$/.exec(before);
    if (m) {
      return { from: context.pos - m[1].length, options: BREAKS.map(b => ({ label: b, info: help.breaks?.[b], type: 'keyword' })), validFor: /^[a-z]*$/ };
    }

    return null;
  };
}
