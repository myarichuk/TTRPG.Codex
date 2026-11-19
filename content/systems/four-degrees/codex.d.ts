// codex.d.ts for four-degrees (Four Degrees), written by Codex.SystemCheck --types. Regenerate it when system.yaml changes.
// What a script beside system.yaml is handed and may return. Everything is plain data, made afresh for each call:
// changing it changes nothing. A script keeps no state between calls, and rolls only through ctx.d.

export type AttributeKey = "str" | "dex" | "con" | "int" | "wis" | "cha";
export type TrackKey = "heroPoints" | (string & {});
export type ValueKey = "senseDc" | "speed";
export type ConditionName = "Blinded" | "Broken" | "Clumsy" | "Concealed" | "Confused" | "Controlled" | "Dazzled" | "Deafened" | "Doomed" | "Drained" | "Dying" | "Encumbered" | "Enfeebled" | "Fascinated" | "Fatigued" | "Fleeing" | "Friendly" | "Frightened" | "Grabbed" | "Helpful" | "Hidden" | "Hostile" | "Immobilized" | "Indifferent" | "Invisible" | "Observed" | "Off-guard" | "Paralyzed" | "Persistent Damage" | "Petrified" | "Prone" | "Quickened" | "Restrained" | "Sickened" | "Slowed" | "Stunned" | "Stupefied" | "Unconscious" | "Undetected" | "Unfriendly" | "Unnoticed" | "Wounded";
export type FunctionName = "degree";
export type RollKey = "degreeCheck" | "strike";
export type EventType = "turnStart" | "sessionStart";
export type Plain = number | string | boolean | null;

/** One pick of the list "trinkets" (Trinkets): the entry's id and its per-pick fields. */
export interface TrinketsPick {
  readonly id: string;
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
    readonly heroPoints: number;
    readonly [key: string]: number | undefined;
  };
  readonly lists: {
    readonly trinkets: readonly TrinketsPick[];
  };
  /** The conditions that hold, those the tracks' thresholds bring included. */
  readonly conditions: readonly ConditionName[];
  /** Each valued condition's number by name; 0 when it does not hold. */
  readonly conditionValues: { readonly [key in "Clumsy" | "Doomed" | "Drained" | "Dying" | "Enfeebled" | "Frightened" | "Sickened" | "Slowed" | "Stunned" | "Stupefied" | "Wounded"]?: number };
  /** Each of the system's resources and its maximum, by key, as numbers (0 when the sheet has none). */
  readonly resources: { readonly [key in "HP" | "HP_Max"]?: number };
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
  /** A valued condition: add with value sets it, add with by raises or lowers it, remove sets it to 0 (always within 0 and its maximum). */
  | { condition: { add: ConditionName; value?: number; by?: number; actor?: string } | { remove: ConditionName; actor?: string } }
  | { set: { list: "trinkets"; key: string; field: string; to: Plain; actor?: string } }
  | { adjust: { list: "trinkets"; key: string; field: string; by: number; actor?: string } }
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
//   rules.js#onTurnStart: the turnStart handler (Handler)
