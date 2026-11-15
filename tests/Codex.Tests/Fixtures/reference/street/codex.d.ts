// codex.d.ts for ref-street (Reference street homebrew (test fixture)), written by Codex.SystemCheck --types. Regenerate it when system.yaml changes.
// What a script beside system.yaml is handed and may return. Everything is plain data, made afresh for each call:
// changing it changes nothing. A script keeps no state between calls, and rolls only through ctx.d.

export type AttributeKey = "might" | "grit" | "speed" | "finesse" | "knack" | "wits";
export type TrackKey = "Vigor" | "Composure" | "Guard_head" | "Guard_torso" | "Fading" | "XP" | (string & {});
export type ValueKey = "Vigor_Max" | "Composure_Max" | "rapport" | "Guard_head_Max" | "Guard_torso_Max" | "skills.pistols" | "skills.fistfight" | "skills.running" | "skills.notice";
export type ConditionName = "Battered" | "Bleeding out";
export type FunctionName = "vigor" | "advanceCost";
export type RollKey = "check" | "lastStand";
export type EventType = "damage" | "turnStart" | "roll" | "advance";
export type Plain = number | string | boolean | null;

/** One entry of the keyed list "skills" (Skills): its fields, and what derive works out. */
export interface SkillsEntry {
  readonly level: number;
  readonly value?: number;
  readonly [field: string]: Plain | undefined;
}
/** One pick of the list "implants" (Implants): the entry's id and its per-pick fields. */
export interface ImplantsPick {
  readonly id: string;
  readonly [field: string]: Plain | undefined;
}
/** One pick of the list "armor" (Armor): the entry's id and its per-pick fields. */
export interface ArmorPick {
  readonly id: string;
  readonly location?: "head" | "torso";
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
    readonly Vigor: number;
    readonly Composure: number;
    readonly Guard_head: number;
    readonly Guard_torso: number;
    readonly Fading: number;
    readonly XP: number;
    readonly [key: string]: number | undefined;
  };
  readonly lists: {
    readonly skills: { readonly [key: string]: SkillsEntry | undefined };
    readonly implants: readonly ImplantsPick[];
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
  | { set: { list: "skills" | "implants" | "armor"; key: string; field: string; to: Plain; actor?: string } }
  | { adjust: { list: "skills" | "implants" | "armor"; key: string; field: string; by: number; actor?: string } }
  | { roll: { definition: RollKey; actor?: string; [input: string]: unknown } }
  | { ask: { who: "gm" | "player"; choices: string[] } }
  | { log: { key: string; args?: Plain[] } }
  /** The creature dies: its dead condition, and what it held and the effects counted on its turns end. */
  | { kill: { actor?: string; condition?: ConditionName } }
  /** Every effect the creature holds up by concentrating ends, on whichever creature carries it. */
  | { endConcentration: { actor?: string } }
  /** A tally the table keeps beside the sheet (death saves). */
  | { counter: { key: string; to: number; actor?: string } | { key: string; by: number; actor?: string } | { key: string; clear: true; actor?: string } }
  ;

/** A function's result: a number, or one with its working. */
export type Value = number | { value: number; parts?: [label: string, amount: number][] };
export type ScriptFunction = (...args: Plain[]) => Value;
/** An event handler: what it returns is applied in order; nothing it changes on its arguments is kept. */
export type Handler = (ev: Event, sheet: Sheet, ctx: Context) => Intent[];

// The exports system.yaml names:
//   rules.js#vigor: function vigor (ScriptFunction)
//   rules.js#onDamage: the damage handler (Handler)
//   rules.js#onAdvance: the advance handler (Handler)
