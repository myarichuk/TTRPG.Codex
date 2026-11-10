// Pictures in the text (src/chips.js, docs/BOOK_EXPORT.md 53): where a chip stands, what it shows, and the one edit that puts a picture block
// under an entry's heading. The chip itself never changes the text.
import assert from 'node:assert/strict';
import { EditorState } from '@codemirror/state';
import { entryPlaces, referenced, placeEdit, chipState, cut, artHints, chips, setChips } from './src/chips.js';

const A = 's' + 'a'.repeat(24), B = 's' + 'b'.repeat(24), C = 's' + 'c'.repeat(24);
const text = [
  '{{chapter,#ch-cast', '# Cast', '}}', '',
  `{{entry,#e-${A}}}`, '## Vera', '', 'A smuggler.', '',
  '```', `{{entry,#e-${C}}}`, '## Not an entry', '```', '',
  `{{entry,#e-${B}}}`, '## Orin', '', '{{portrait', `![Orin](art:${B})`, '}}', '', 'A lamplighter.', ''
].join('\n');

// An entry is its marker and the heading under it; a marker inside a code fence is text.
const places = entryPlaces(text);
assert.deepEqual(places.map(p => p.key), [A, B]);
assert.equal(text.slice(places[0].from, places[0].at), `{{entry,#e-${A}}}\n## Vera`);
assert.equal(text.slice(places[1].from, places[1].at), `{{entry,#e-${B}}}\n## Orin`);
// No heading under the marker (the GM deleted it): the chip stands under the marker.
assert.equal(entryPlaces(`{{entry,#e-${A}}}\nWords.`)[0].at, `{{entry,#e-${A}}}`.length);
// Not a marker: one typed mid-line, one with a key of another shape.
assert.deepEqual(entryPlaces(`text {{entry,#e-${A}}}\n{{entry,#e-zzz}}\n{{entry,#e-${A}}} more`), []);

assert.equal(referenced(text, B), true);
assert.equal(referenced(text, A), false);

// The block goes under the heading, a blank line on each side, exactly as the generator writes it; never twice, never for an entry not there.
const block = `{{portrait\n![Vera](art:${A})\n}}`;
const edit = placeEdit(text, A, block);
const placed = text.slice(0, edit.at) + edit.insert + text.slice(edit.at);
assert.ok(placed.includes(`## Vera\n\n${block}\n\nA smuggler.`), placed);
assert.equal(placed.length, text.length + block.length + 2);
assert.equal(placeEdit(placed, A, block), null, 'a chapter that already points at the picture is left alone');
assert.equal(placeEdit(text, C, block), null, 'a marker in a code fence takes no picture');
assert.equal(placeEdit(text, 's' + 'd'.repeat(24), block), null);
// A heading with text straight under it, and a heading that ends the chapter: the block still stands apart.
let tight = `{{entry,#e-${A}}}\n## Vera\nA smuggler.`;
let e = placeEdit(tight, A, block);
assert.equal(tight.slice(0, e.at) + e.insert + tight.slice(e.at), `{{entry,#e-${A}}}\n## Vera\n\n${block}\n\nA smuggler.`);
tight = `{{entry,#e-${A}}}\n## Vera`;
e = placeEdit(tight, A, block);
assert.equal(tight.slice(0, e.at) + e.insert, `{{entry,#e-${A}}}\n## Vera\n\n${block}\n`);

// What a chip shows.
const idea = { depicts: 'x', stale: false, prompt: 'p', problems: [] };
assert.equal(chipState({ key: A, section: 'cast', pictured: true }, true, {}), null, 'a picture that is in the text needs no chip');
assert.equal(chipState({ key: A, section: 'cast', pictured: true }, false, {}), 'ready');
assert.equal(chipState({ key: A, section: 'cast', pictured: false }, false, {}), 'empty');
assert.equal(chipState({ key: A, section: 'cast', pictured: false, idea }, false, {}), 'idea');
assert.equal(chipState({ key: A, section: 'cast', pictured: false, idea: { ...idea, stale: true } }, false, {}), 'stale');
assert.equal(chipState({ key: A, section: 'cast', pictured: false, idea }, false, { busy: { kind: 'draw', keys: [A] } }), 'drawing');
assert.equal(chipState({ key: A, section: 'cast', pictured: false, idea }, false, { busy: { kind: 'draw', keys: [B] } }), 'idea');
assert.equal(chipState({ key: A, section: 'cast', pictured: false }, false, { busy: { kind: 'suggest', sections: ['cast'] } }), 'suggesting');
assert.equal(chipState({ key: A, section: 'cast', pictured: false }, false, { busy: { kind: 'suggest', sections: ['lore'] } }), 'empty');
// A place that has a current suggestion keeps showing it while the chapter's other places are asked about.
assert.equal(chipState({ key: A, section: 'cast', pictured: false, idea }, false, { busy: { kind: 'suggest', sections: ['cast'] } }), 'idea');

// The suggestion's words, cut at 60 characters.
assert.equal(cut('A short one'), 'A short one');
const long = cut('A weathered smuggler leaning on a harbour rail at dusk, lantern light on wet stone and rope');
assert.ok(long.length <= 61 && long.endsWith('…'), long);
assert.ok(!/\s…$/.test(long));
assert.equal(cut('x'.repeat(80)).length, 61);

// "art:KEY" names the place it points at; a key nobody knows gets no name.
const names = new Map([[B, 'Orin']]);
const hints = artHints(text, names);
assert.equal(hints.length, 1);
assert.equal(text.slice(hints[0].at - 4 - 25 - 1, hints[0].at), 'art:' + B + ')', 'the name stands after the closing bracket');
assert.equal(artHints(`art:${B} loose`, names)[0].at, 4 + 25);
assert.deepEqual(artHints(`![x](art:${A})`, names), []);

// In the editor: one chip under Vera's heading (Orin's picture is in the text), a name after Orin's key, and the text untouched by either.
let data = { chips: [{ key: A, section: 'cast', label: 'Vera', block, pictured: false, idea: null }, { key: B, section: 'cast', label: 'Orin', block: '', pictured: true, idea: null }], model: true };
const ctx = { data: () => data, labels: () => ({}), names, act() {}, quote() {}, upload() {}, place() {} };
const extensions = chips(ctx);
let state = EditorState.create({ doc: text, extensions });
const field = extensions[0];
const widgets = s => { const out = []; s.field(field).between(0, s.doc.length, (from, to, deco) => { out.push({ from, block: !!deco.spec.block }); }); return out; };
let w = widgets(state);
assert.deepEqual(w.filter(x => x.block).map(x => x.from), [places[0].at]);
assert.equal(w.filter(x => !x.block).length, 1);
assert.equal(state.doc.toString(), text);
// The server says Vera now has a picture, and the text does not point at it: the chip stays, to put it in. Once the text does, no chip.
data = { ...data, chips: [{ ...data.chips[0], pictured: true }, data.chips[1]] };
state = state.update({ effects: setChips.of(null) }).state;
assert.equal(widgets(state).filter(x => x.block).length, 1);
state = state.update({ changes: { from: edit.at, insert: edit.insert } }).state;
assert.equal(widgets(state).filter(x => x.block).length, 0);
assert.equal(widgets(state).filter(x => !x.block).length, 1, 'Vera\'s key has no name here (not in the list), Orin\'s has');

console.log('chips: ok');
