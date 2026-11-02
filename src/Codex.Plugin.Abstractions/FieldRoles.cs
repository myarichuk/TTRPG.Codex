namespace Codex.Plugin.Abstractions;

/// <summary>
/// The actor fields the app reads by what they mean rather than by name, so a system calls them what it likes. A role left
/// empty turns off what reads it: without <see cref="Level"/> there is no level up, without <see cref="Speed"/> the
/// sheet's plate shows no speed. Each names a key of <see cref="SystemDefinition.ActorFields"/>.
/// </summary>
public class FieldRoles
{
    /// <summary>The character's level: the level up, the AI's level-up suggestion, Cast's summary.</summary>
    public string? Level { get; set; }

    /// <summary>The character's (first) class and its subclass, for the AI's level-up suggestion and the table's hit dice.</summary>
    public string? Class { get; set; }
    public string? Subclass { get; set; }

    /// <summary>Shown on the sheet's plate beside the derived numbers.</summary>
    public string? Speed { get; set; }

    /// <summary>The fields Cast's one-line summary of a character lists, in order. <see cref="Level"/> reads "Level 3"; a
    /// reference field reads its entry's name.</summary>
    public List<string> Summary { get; set; } = new();

    public IEnumerable<string> Named => new[] { Level, Class, Subclass, Speed }.Concat(Summary).OfType<string>();
}
