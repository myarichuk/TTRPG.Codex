// The review's diff (src/review-diff.js): a chapter rewritten throughout is still one change per entry, which the GM accepts or rejects alone.
import assert from 'node:assert/strict';
import { Chunk, diff } from '@codemirror/merge';
import { Text } from '@codemirror/state';
import { entryDiff } from './src/review-diff.js';

const entry = (id, name, body) => `{{entry,#e-${id}}}\n## ${name}\n\n${body}\n\n`;
const words = n => Array.from({ length: n }, (_, i) => `word${i}`).join(' ');
const reversed = n => words(n).split(' ').reverse().join(' ');
const head = '{{chapter,#ch-cast\n# Cast\n}}\n\n';
const before = head + entry('a', 'Oren', 'Rows the ferry twice a day. ' + words(80)) + entry('b', 'Vera', 'Keeps the lamp. ' + words(80)) +
  entry('c', 'Stranger', 'A grey hood. ' + words(80));
const after = head + entry('a', 'Oren', 'Oren rows the ferry between the points twice a day, ' + reversed(60)) +
  entry('b', 'Vera', 'Vera keeps the lamp burning, ' + reversed(70)) + entry('c', 'Stranger', 'Wrapped in a grey hood, ' + reversed(50));

const doc = text => Text.of(text.split('\n'));
const apply = (a, b, changes) => {
  let out = '', at = 0;
  for (const c of changes) { out += a.slice(at, c.fromA) + b.slice(c.fromB, c.toB); at = c.toA; }
  return out + a.slice(at);
};

// One change per entry, where the library's own default makes fewer (here: the whole chapter as one).
assert.equal(Chunk.build(doc(before), doc(after), { override: entryDiff }).length, 3, 'one change per entry');
const theirs = Chunk.build(doc(before), doc(after), { scanLimit: 500 }).length;
assert.ok(theirs < 3, 'the default diff gave ' + theirs);

// The changes give the reply back.
assert.equal(apply(before, after, entryDiff(before, after)), after);

// Markers that do not line up (an entry added): the whole text is diffed, still correctly.
const added = after + entry('d', 'Morrow', 'New.');
assert.equal(apply(before, added, entryDiff(before, added)), added);
assert.deepEqual(entryDiff('x', 'x'), diff('x', 'x'));
console.log('review diff: ok');
