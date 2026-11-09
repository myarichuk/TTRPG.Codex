// The diff behind an AI edit's review (docs/BOOK_EXPORT.md 42): one change per entry, never one change for the whole chapter.
//
// The merge view's own diff gives up on precision when most of the text changed (its default scan limit), and a chapter rewritten "as the
// book" changes nearly every line: it then reports the chapter as one change, which the GM can only accept or reject whole. Here both texts
// are cut at the entry markers they share (`{{entry,#e-…}}` lines, which the program keeps), each entry is diffed on its own with a higher
// limit, and the changes are put back at the entries' offsets. The markers themselves are then unchanged lines between the entries'
// changes, so each entry is a change of its own. Texts whose markers do not line up (a merge's added or removed entries) are diffed whole.
import { Change, diff } from '@codemirror/merge';

const MARKER = /^\{\{entry,#[^}\n]*\}\}[ \t]*$/gm;
const PRECISE = { scanLimit: 20000, timeout: 400 };

// Where each part starts: the text's start, then each entry marker's line.
function cuts(text) {
  const at = [0];
  for (const m of text.matchAll(MARKER)) if (m.index > 0) at.push(m.index);
  return at;
}

function markers(text, at) {
  return at.slice(1).map(i => { const end = text.indexOf('\n', i); return text.slice(i, end < 0 ? text.length : end).trim(); });
}

export function entryDiff(a, b) {
  const ka = cuts(a), kb = cuts(b);
  if (ka.length < 2 || ka.length !== kb.length || markers(a, ka).join('\n') !== markers(b, kb).join('\n')) return diff(a, b, PRECISE);
  const changes = [];
  for (let i = 0; i < ka.length; i++) {
    const a0 = ka[i], a1 = i + 1 < ka.length ? ka[i + 1] : a.length;
    const b0 = kb[i], b1 = i + 1 < kb.length ? kb[i + 1] : b.length;
    for (const c of diff(a.slice(a0, a1), b.slice(b0, b1), PRECISE)) changes.push(new Change(c.fromA + a0, c.toA + a0, c.fromB + b0, c.toB + b0));
  }

  return changes;
}

/** The merge view's diff settings for a review. */
export const reviewDiffConfig = { override: entryDiff };
