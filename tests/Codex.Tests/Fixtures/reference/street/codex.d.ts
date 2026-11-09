// codex.d.ts for ref-street (Reference street homebrew (test fixture)), written by Codex.SystemCheck --types. Regenerate it when system.yaml changes.
// What a script beside system.yaml is handed and may return. Everything is plain data, made afresh for each call:
// changing it changes nothing. A script keeps no state between calls, and rolls only through ctx.d.

export type AttributeKey = "BODY" | "WILL" | "REF" | "DEX" | "TECH" | "INT";
export type TrackKey = "HP" | "Humanity" | "SP_head" | "SP_body" | "DeathPenalty" | "IP" | (string & {});
export type ValueKey = "HP_Max" | "Humanity_Max" | "EMP" | "SP_head_Max" | "SP_body_Max" | "skills.handgun" | "skills.brawling" | "skills.athletics" | "skills.perception";
export type ConditionName = "Seriously wounded" | "Mortally wounded";
export type FunctionName = "hitPoints" | "advanceCost";
export type RollKey = "check" | "deathSave";
export type EventType = "damage" | "turnStart" | "roll" | "advance";
export type Plain = number | string | boolean | null;

/** One entry of the keyed list "skills" (Skills): its fields, and what derive works out. */
export interface SkillsEntry {
  readonly level: number;
  readonly value?: number;
  readonly [field: string]: Plain | undefined;
}
/** One pick of the list "cyberware" (Implants): the entry's id and its per-pick fields. */
export interface CyberwarePick {
  readonly id: string;
  readonly [field: string]: Plain | undefined;
}
/** One pick of the list "armor" (Armor): the entry's id and its per-pick fields. */
export interface ArmorPick {
  readonly id: string;
  readonly location?: "head" | "body";
  readonly [field: string]: Plain | undefined;
}

/** The sheet an event is on. */
export interface Sheet {
  readonly kind: string | null;
  readonly attributes: { readonly [key in AttributeKey]: number };
  /** Every value of the sheet kind; a keyed list's entries as "list.key". */
  readonly values: { readonly [key in ValueKey]?: number | string };
  /** Each track's current number; a slot track's is what its slots hold. */
  readonly tracks: {
    readonly HP: number;
    readonly Humanity: number;
    readonly SP_head: number;
    readonly SP_body: number;
    readonly DeathPenalty: number;
    readonly IP: number;
    readonly [key: string]: number | undefined;
  };
  readonly lists: {
    readonly skills: { readonly [key: string]: SkillsEntry | undefined };
    readonly cyberware: readonly CyberwarePick[];
    readonly armor: readonly ArmorPick[];
  };
  /** The conditions that hold, those the tracks' thresholds bring included. */
  readonly conditions: readonly ConditionName[];
}

/** An event's fields, as the table or the GM gave them: numbers, text, flags, and lists and maps of them. What
 * fields an event has is the system's to say, so they are not typed here. */
export interface Event { readonly [field: string]: any }

export interface Context {
  /** Calls one of the system's named functions, formula or script. */
  call(name: FunctionName, ...args: Plain[]): number;
  /** Rolls one die through the table, which logs the face. Math.random is not available. */
  d(sides: number): number;
}

/** What a handler returns: intents, applied in order. actor names another actor's sheet; none is this one. */
export type Intent =
  | { adjust: { track: TrackKey; by: number; actor?: string } }
  | { set: { track: TrackKey; to: number; actor?: string } }
  | { mark: { track: TrackKey; by?: number; level?: number; text?: string; actor?: string } }
  | { clear: { track: TrackKey; by?: number; level?: number; text?: string; actor?: string } }
  | { condition: { add: ConditionName; actor?: string } | { remove: ConditionName; actor?: string } }
  | { set: { list: "skills" | "cyberware" | "armor"; key: string; field: string; to: Plain; actor?: string } }
  | { adjust: { list: "skills" | "cyberware" | "armor"; key: string; field: string; by: number; actor?: string } }
  | { roll: { definition: RollKey; actor?: string; [input: string]: unknown } }
  | { ask: { who: "gm" | "player"; choices: string[] } }
  | { log: { key: string; args?: Plain[] } }
  ;

/** A function's result: a number, or one with its working. */
export type Value = number | { value: number; parts?: [label: string, amount: number][] };
export type ScriptFunction = (...args: Plain[]) => Value;
/** An event handler: what it returns is applied in order; nothing it changes on its arguments is kept. */
export type Handler = (ev: Event, sheet: Sheet, ctx: Context) => Intent[];

// The exports system.yaml names:
//   rules.js#hitPoints: function hitPoints (ScriptFunction)
//   rules.js#onDamage: the damage handler (Handler)
//   rules.js#onAdvance: the advance handler (Handler)
