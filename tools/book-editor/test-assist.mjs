// The editor's suggestions (src/assist.js, docs/BOOK_EXPORT.md 47): what may follow "{{", a picture block's frames, a break, and this edition's
// pictures after "art:", found by their labels and typed as keys; the party edition is never offered a secret block.
import assert from 'node:assert/strict';
import { EditorState } from '@codemirror/state';
import { CompletionContext } from '@codemirror/autocomplete';
import { completions } from './src/assist.js';

const help = { blocks: { note: 'A note.' }, frames: { torn: 'Torn.' }, spot: 'Small picture after {0}' };
const slots = [{ key: 'sAAA', kind: 'entry', label: 'Vera' }, { key: 'sBBB', kind: 'spot', label: 'Vera' }, { key: 'sCCC', kind: 'cover', label: 'Cover' }];
const at = (doc, dm = false) => {
  const state = EditorState.create({ doc });
  return completions(help, slots, dm)(new CompletionContext(state, doc.length, false));
};
const labels = r => r.options.map(o => o.label);

// A block's name after "{{" at a line's start; the party edition has no secret block, the GM's has.
let r = at('Some text.\n{{');
assert.ok(labels(r).includes('note') && labels(r).includes('portrait'));
assert.ok(!labels(r).includes('secret'), 'the party edition is offered no secret block');
assert.ok(labels(at('{{', true)).includes('secret'));
assert.equal(r.options.find(o => o.label === 'note').info, 'A note.');
assert.ok(!labels(r).some(l => ['chapter', 'entry', 'properties', 'cover-art'].includes(l)), 'the program\'s own blocks are not offered');
assert.equal(at('{{no').from, 2);
// Not in the middle of a line.
assert.equal(at('text {{'), null);

// A picture block's frames, not one already given; a stat block's frame; nothing for a note.
assert.deepEqual(labels(at('{{portrait,')), ['torn', 'oval', 'round']);
assert.deepEqual(labels(at('{{spot,oval,')), ['torn', 'round']);
assert.deepEqual(labels(at('{{monster,')), ['frame']);
assert.equal(at('{{note,'), null);

// Breaks.
assert.deepEqual(labels(at('\\')), ['page', 'column']);
assert.equal(at('\\pa').from, 1);

// Pictures: by their labels (a spot as the small picture after its entry), typed as the key and the closing bracket.
r = at('![Vera](art:');
assert.deepEqual(labels(r), ['Vera', 'Small picture after Vera', 'Cover']);
// Picking one types its key and one closing bracket, whether or not the editor had already closed it.
for (const doc of ['![Vera](art:Ve', '![Vera](art:Ve)']) {
  const pos = '![Vera](art:Ve'.length;
  let state = EditorState.create({ doc, selection: { anchor: pos } });
  const result = completions(help, slots, false)(new CompletionContext(state, pos, false));
  const view = { state, dispatch(tr) { state = state.update(tr).state; this.state = state; } };
  result.options[0].apply(view, result.options[0], result.from, pos);
  assert.equal(view.state.doc.toString(), '![Vera](art:sAAA)');
  assert.equal(view.state.selection.main.head, '![Vera](art:sAAA)'.length);
}
assert.equal(at('![Vera](art:Ve').from, '![Vera](art:'.length);
assert.equal(at('plain words'), null);

console.log('assist: ok');
