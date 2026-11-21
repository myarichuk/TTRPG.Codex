using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

// The consistency checker (docs/WORLD_BUILDER.md, section 9) reads a campaign and returns issues; a game system may add checks of its own, in
// two layers. Layer 2 is data: an `issueChecks` entry in system.yaml with a `rule` (required, empty, range, reference, count), over the sheet's
// own keys. Layer 3 is code: an `issueChecks` entry with a `type`, a class in a DLL beside system.yaml that implements ICodexIssueCheck.
//
//   issueChecks:
//     - { id: wizard-needs-spells, rule: required, field: spells, when: { field: class, is: wizard },
//         severity: warning, message: "{name} is a wizard with no {field}." }
//     - { id: level-cap, rule: range, field: level, max: 10, severity: error, message: "{name} is level {value}; this table caps at {max}." }
//     - { id: house-check, type: "Acme.Street.HouseCheck, Acme.Street" }
//
// Neither layer asks a model anything: the checks are code, and the same sheet gives the same issues. A system's checks run beside the app's
// own, are listed by list_issues, dismissed and resolved like theirs, and one that cannot run is an `info` issue of its own ("check-failed"),
// never a failure of the run.

/// <summary>
/// One check a game system adds to the consistency checker. Either a <see cref="Rule"/> (data, layer 2) over a sheet key, or a compiled
/// <see cref="Type"/> (code, layer 3); one of them. Keys it does not read, a rule it does not know and a field that is not a sheet key
/// refuse the system at load, with a sentence naming the check.
/// </summary>
public sealed class IssueCheckDefinition
{
    /// <summary>Required: the check's name, lower-case letters, digits, <c>-</c> and <c>_</c>, unique in the system. It is part of every issue's id
    /// (<c>system-rule:actor:&lt;id&gt;:&lt;field&gt;:&lt;id&gt;</c>), so renaming a check makes its issues new issues.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The data rule: <c>required</c> (the field is filled in), <c>empty</c> (the field is empty), <c>range</c> (a number between
    /// <c>min</c> and <c>max</c>), <c>reference</c> (the field names a Library entry that exists) or <c>count</c> (a list holds between
    /// <c>min</c> and <c>max</c> entries). With <c>when</c> a rule only applies to sheets where the condition holds (<c>required</c> + <c>when</c>
    /// is "required when", <c>empty</c> + <c>when</c> is "empty when"). Leave out when <c>type</c> names compiled code.</summary>
    public string? Rule { get; set; }

    /// <summary>A compiled check, <c>"Namespace.Type, Assembly"</c>, from a DLL beside system.yaml: a public class with a public parameterless
    /// constructor that implements <c>ICodexIssueCheck</c>. It is given plain copies of the campaign's cast, pages and places and returns issues
    /// as plain data; one that throws or returns what cannot be read becomes one <c>info</c> issue, "check-failed". Not with <c>rule</c>.</summary>
    public string? Type { get; set; }

    /// <summary>For a rule: the sheet key it is about, one of the system's attributes, actor fields, its inventory key or its list keys.</summary>
    public string? Field { get; set; }

    /// <summary>For a rule: how serious a finding is, <c>error</c>, <c>warning</c> (the default) or <c>info</c>.</summary>
    public string Severity { get; set; } = "warning";

    /// <summary>For a rule: the sentence for the game master and the model, in English. <c>{name}</c> is the character's name, <c>{field}</c> the
    /// field's label, <c>{value}</c> what the field holds (the number of entries for <c>count</c>), <c>{min}</c> and <c>{max}</c> the limits. Required.</summary>
    public string? Message { get; set; }

    /// <summary>For a rule: what to do about it, in English, for the model: advice, never an instruction to invent content. A plain default is used when empty.</summary>
    public string? FixHint { get; set; }

    /// <summary>For a rule: the localisation key the app shows the sentence by (<c>Consistency.System.Wizard</c>); the app falls back to
    /// <c>message</c> where it has no text for the key. Default <c>Consistency.System.&lt;id&gt;</c>.</summary>
    public string? Code { get; set; }

    /// <summary>For a rule: whose sheets it looks at, any of <c>pc</c> (player characters), <c>npc</c> and <c>monster</c>. Default <c>pc</c> and <c>npc</c>.
    /// A retired character is never checked.</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>For a rule: only sheets where this holds are judged.</summary>
    public IssueCondition? When { get; set; }

    /// <summary>For <c>range</c> and <c>count</c>: the lowest value or count that is fine. At least one of <c>min</c> and <c>max</c>.</summary>
    public double? Min { get; set; }

    /// <summary>For <c>range</c> and <c>count</c>: the highest value or count that is fine.</summary>
    public double? Max { get; set; }

    /// <summary>For <c>reference</c>: the content type the value is an entry of. Default: the field's own <c>targetEntityType</c>, or the
    /// inventory's or list's types.</summary>
    public string? TargetEntityType { get; set; }
}

/// <summary>A condition on one sheet key, for <see cref="IssueCheckDefinition.When"/>. Exactly one of <c>is</c>, <c>in</c>, <c>notEmpty</c> and <c>empty</c>.</summary>
public sealed class IssueCondition
{
    /// <summary>Required: the sheet key it reads.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>The key's value is this (compared as text, any case; a number as it is written).</summary>
    public string? Is { get; set; }

    /// <summary>The key's value is one of these.</summary>
    public List<string>? In { get; set; }

    /// <summary>True: the key holds something (a non-empty text or list, any number).</summary>
    public bool? NotEmpty { get; set; }

    /// <summary>True: the key holds nothing.</summary>
    public bool? Empty { get; set; }
}

/// <summary>
/// A compiled check (layer 3): a class beside system.yaml, named by an <c>issueChecks</c> entry's <c>type</c>. It reads plain data and says what is wrong
/// as plain data; the host makes the issues' ids, sorts them and shows them. It must give the same answer for the same world (no clock, no random, no
/// network), keep no state (one instance serves every run, from any thread) and not take long: nothing stops it, as nothing stops any compiled code, which
/// runs with the server's rights and is installed only by copying it onto the server. One that throws becomes a single <c>info</c> issue.
/// </summary>
public interface ICodexIssueCheck
{
    /// <summary>The issues found in <paramref name="world"/>; none when all is well. At most 200 are read.</summary>
    IReadOnlyList<CodexIssue> Check(CodexIssueWorld world);
}

/// <summary>One thing a compiled check found. <see cref="Kind"/> and <see cref="EntityId"/> name what it is about (the ids of <see cref="CodexIssueWorld"/>);
/// <see cref="Detail"/> tells two findings about one field apart (a short slug); the host builds the issue's id from them.</summary>
/// <param name="Kind"><c>actor</c>, <c>page</c> or <c>place</c>.</param>
/// <param name="EntityId">The id of the actor, page or place, as the world gave it.</param>
/// <param name="Field">The sheet key or field it is about, or <c>-</c> for the thing as a whole.</param>
/// <param name="Severity"><c>error</c>, <c>warning</c> or <c>info</c>.</param>
/// <param name="Code">A localisation key for the sentence, such as <c>Consistency.System.NoGear</c>.</param>
/// <param name="Args">What fills the blanks of the sentence for the code, as text, in order.</param>
/// <param name="Message">The sentence in English, for the game master and the model.</param>
/// <param name="FixHint">What to do, in English; advice, never an instruction to invent content.</param>
/// <param name="Detail">A short slug that keeps two findings about one field apart; none when there is one.</param>
public sealed record CodexIssue(string Kind, string EntityId, string Field, string Severity, string Code, IReadOnlyList<string> Args, string Message, string? FixHint = null, string? Detail = null);

/// <summary>What a compiled check reads: plain copies, made afresh for each run; what it changes on them is not kept.</summary>
/// <param name="SystemId">The campaign's game system.</param>
public sealed record CodexIssueWorld(string SystemId, IReadOnlyList<CodexIssueActor> Actors, IReadOnlyList<CodexIssuePage> Pages, IReadOnlyList<CodexIssuePlace> Places);

/// <param name="Role"><c>pc</c>, <c>npc</c> or <c>monster</c>.</param>
/// <param name="Sheet">The sheet's values by key: numbers, text, flags and lists of them.</param>
public sealed record CodexIssueActor(string Id, string Name, string Role, bool Retired, IReadOnlyDictionary<string, object?> Sheet);

public sealed record CodexIssuePage(string Id, string Title, string Kind, bool PartyVisible, IReadOnlyList<string> Tags);

public sealed record CodexIssuePlace(string Id, string RegionId, string Name, string? ParentId);
