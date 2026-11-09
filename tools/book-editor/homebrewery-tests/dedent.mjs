// Enough of the dedent package for Homebrewery's tests: the template's common indentation and its edge blank lines removed.
export default function dedent(strings, ...values) {
  const raw = typeof strings === 'string' ? [strings] : strings.raw;
  let s = raw.reduce((acc, str, i) => acc + str.replace(/\\`/g, '`').replace(/\\\$/g, '$') + (i < values.length ? values[i] : ''), '');
  const lines = s.split('\n');
  const indent = Math.min(...lines.filter(l => l.trim()).map(l => l.match(/^[ \t]*/)[0].length));
  s = lines.map(l => l.slice(Math.min(indent, l.match(/^[ \t]*/)[0].length))).join('\n').trim();
  return s.replace(/\\n/g, '\n');
}
