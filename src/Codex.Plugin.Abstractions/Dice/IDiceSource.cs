using System.Collections.Generic;

namespace Codex.Plugin.Abstractions.Dice;

/// <summary>
/// Where faces come from. Rolling (<see cref="StandardDiceRoller"/>, roll definitions) asks it for one face at a time, so a
/// test, a replay or a fixture hands out exactly the faces it names (<see cref="SequenceDiceSource"/>) and everything else
/// rolls at random (<see cref="RandomDiceSource"/>).
/// </summary>
public interface IDiceSource
{
    /// <summary>A face from 1 to <paramref name="sides"/>.</summary>
    int Roll(int sides);
}

public sealed class RandomDiceSource(System.Random? random = null) : IDiceSource
{
    public static readonly RandomDiceSource Shared = new();

    private readonly System.Random _random = random ?? System.Random.Shared;

    public int Roll(int sides) => _random.Next(1, sides + 1);
}

/// <summary>Hands out the given faces in order. Running out, or a face the die does not have, is the caller's mistake and
/// throws, so a test never passes on dice it did not name.</summary>
public sealed class SequenceDiceSource(IEnumerable<int> faces) : IDiceSource
{
    private readonly Queue<int> _faces = new(faces);

    /// <summary>How many faces have been handed out.</summary>
    public int Used { get; private set; }

    public int Remaining => _faces.Count;

    public int Roll(int sides)
    {
        if (_faces.Count == 0) throw new System.InvalidOperationException($"no more faces to hand out (after {Used})");
        var face = _faces.Dequeue();
        if (face < 1 || face > sides) throw new System.InvalidOperationException($"a d{sides} cannot show {face}");
        Used++;
        return face;
    }
}
