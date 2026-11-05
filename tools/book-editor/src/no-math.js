// Stands in for expr-eval in the bundle. marked-hbfm's variables extension evaluates "$[...]" as maths with expr-eval, whose every version has
// critical advisories (code execution, prototype pollution) and no fix; and a draft holds text players wrote, rendered in the GM's own page.
// This parser evaluates nothing: a "$[...]" that would be maths is left to the extension's fallback, plain variables still work.
export class Parser {
  constructor() {
    this.functions = {};
    this.consts = {};
  }

  evaluate() {
    throw new Error('Maths in a book draft is switched off.');
  }

  parse() {
    throw new Error('Maths in a book draft is switched off.');
  }
}

export default { Parser };
