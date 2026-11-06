// Stands in for expr-eval in the bundle. marked-hbfm's variables extension evaluates "$[...]" as maths with expr-eval, whose every version has
// critical advisories (code execution, prototype pollution) and no fix; and a draft holds text players wrote, rendered in the GM's own page.
// This is the maths Homebrewery's brews use and no more (Homebrewery's own variable tests pass on it, tools/book-editor/test.mjs): numbers,
// + - * / ^, brackets, and calls to a fixed list of functions. It reads no name but a listed function's: no properties, no members, no
// strings, no assignment, nothing to reach an object through. Anything else throws, and the extension then leaves the "$[...]" as written.

// Homebrewery's parser turns these on (its unary operators, written "floor x" or "floor(x)"), and expr-eval's own functions that are maths.
const UNARY = { round: Math.round, floor: Math.floor, ceil: Math.ceil, abs: Math.abs };
const DEFAULTS = {
  min: (...a) => Math.min(...a),
  max: (...a) => Math.max(...a),
  pow: (a, b) => Math.pow(a, b),
  hypot: (...a) => Math.hypot(...a),
  roundTo: (a, d) => { const f = Math.pow(10, d || 0); return Math.round(a * f) / f; },
};
const MAX_LENGTH = 500;
const MAX_DEPTH = 40;

export class Parser {
  constructor() {
    // marked-variables adds its own (sign, signed, toRomans, toChar, toWords and their cases) here.
    this.functions = Object.assign(Object.create(null), DEFAULTS);
    this.consts = Object.create(null);
  }

  parse() {
    throw new Error('Only evaluate is supported.');
  }

  evaluate(expression) {
    if (typeof expression !== 'string' || expression.length > MAX_LENGTH) throw new Error('Not maths.');
    const tokens = tokenize(expression);
    let at = 0;
    let depth = 0;
    const peek = () => tokens[at];
    const take = (value) => {
      if (tokens[at]?.value !== value) throw new Error('Expected ' + value);
      at++;
    };
    const call = (fn, args) => {
      const result = fn(...args);
      if (typeof result !== 'number' && typeof result !== 'string') throw new Error('Not maths.');
      return result;
    };
    const fn = (name) => {
      if (Object.hasOwn(UNARY, name)) return UNARY[name];
      if (Object.hasOwn(this.functions, name) && typeof this.functions[name] === 'function') return this.functions[name];
      throw new Error('Unknown function ' + name);
    };

    // expression := term (("+" | "-") term)*; term := unary (("*" | "/") unary)*; unary := "-" unary | "+" unary | power;
    // power := atom ("^" unary)?; atom := number | "(" expression ")" | name "(" expression ("," expression)* ")" | unary-name unary
    function expressionRule() {
      if (++depth > MAX_DEPTH) throw new Error('Too deep.');
      let value = term();
      while (peek()?.value === '+' || peek()?.value === '-') {
        const op = tokens[at++].value;
        const right = term();
        value = op === '+' ? value + right : value - right;
      }
      depth--;
      return value;
    }
    function term() {
      let value = unary();
      while (peek()?.value === '*' || peek()?.value === '/') {
        const op = tokens[at++].value;
        const right = unary();
        value = op === '*' ? value * right : value / right;
      }
      return value;
    }
    function unary() {
      if (peek()?.value === '-') { at++; return -unary(); }
      if (peek()?.value === '+') { at++; return +unary(); }
      return power();
    }
    function power() {
      const base = atom();
      if (peek()?.value === '^') { at++; return Math.pow(base, unary()); }
      return base;
    }
    function atom() {
      const t = tokens[at++];
      if (!t) throw new Error('Unexpected end.');
      if (t.type === 'number') return t.value;
      if (t.value === '(') { const v = expressionRule(); take(')'); return v; }
      if (t.type === 'name') {
        const f = fn(t.value);
        if (peek()?.value !== '(') {
          if (!Object.hasOwn(UNARY, t.value)) throw new Error('Expected (');
          return call(f, [unary()]);
        }
        at++;
        const args = [];
        if (peek()?.value !== ')') {
          args.push(expressionRule());
          while (peek()?.value === ',') { at++; args.push(expressionRule()); }
        }
        take(')');
        return call(f, args);
      }
      throw new Error('Unexpected ' + t.value);
    }

    const value = expressionRule();
    if (at !== tokens.length) throw new Error('Unexpected ' + tokens[at].value);
    return value;
  }
}

function tokenize(text) {
  const tokens = [];
  const re = /\s*(?:(\d+(?:\.\d*)?(?:[eE][+-]?\d+)?|\.\d+(?:[eE][+-]?\d+)?)|([A-Za-z_][A-Za-z0-9_]*)|([-+*/^(),]))/y;
  let at = 0;
  while (at < text.length) {
    if (/^\s*$/.test(text.slice(at))) break;
    re.lastIndex = at;
    const m = re.exec(text);
    if (!m) throw new Error('Not maths.');
    at = re.lastIndex;
    if (m[1] !== undefined) tokens.push({ type: 'number', value: Number(m[1]) });
    else if (m[2] !== undefined) tokens.push({ type: 'name', value: m[2] });
    else tokens.push({ type: 'op', value: m[3] });
  }
  return tokens;
}

export default { Parser };
