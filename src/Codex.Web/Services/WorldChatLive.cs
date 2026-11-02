using System.Text;
using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>A piece of the model's turn as it is written: more of its reply, or more of its reasoning (for models that share it).</summary>
public sealed record WorldChatDelta(bool Reasoning, string Text);

/// <summary>The turn under way, as the page shows it while the model works: the tools it has called (the last may still be
/// running), what it is writing, and its reasoning. Written by the turn, read by the page on another thread, so every read is a
/// copy taken under the lock.</summary>
public sealed class WorldChatLive
{
    private readonly object gate = new();
    private readonly List<WorldChatStep> steps = new();
    private readonly StringBuilder reply = new();
    private readonly StringBuilder reasoning = new();
    private string? aside;
    private bool thinking;

    public DateTime StartedAt { get; } = DateTime.UtcNow;

    public IReadOnlyList<WorldChatStep> Steps
    {
        get { lock (gate) return steps.ToList(); }
    }

    /// <summary>The reply as far as it has arrived.</summary>
    public string Reply
    {
        get { lock (gate) return reply.ToString(); }
    }

    /// <summary>The whole reasoning of the turn so far.</summary>
    public string Reasoning
    {
        get { lock (gate) return reasoning.ToString(); }
    }

    /// <summary>True while the model is reasoning rather than writing.</summary>
    public bool Thinking
    {
        get { lock (gate) return thinking; }
    }

    /// <summary>What the model said before its latest tool call ("Let me look…"): one line, replaced by the next, never a pile.</summary>
    public string? Aside
    {
        get { lock (gate) return aside; }
    }

    internal void Step(WorldChatStep step)
    {
        lock (gate)
        {
            if (steps.Contains(step)) return;
            // A tool call ends the round: the words before it were a preamble, not the answer.
            if (reply.Length > 0) aside = reply.ToString().Trim();
            reply.Clear();
            thinking = false;
            steps.Add(step);
        }
    }

    internal void Delta(WorldChatDelta delta)
    {
        lock (gate)
        {
            if (delta.Reasoning)
            {
                // Reasoning after a stretch of reply is a new round of thinking: a paragraph of its own.
                if (!thinking && reasoning.Length > 0) reasoning.Append("\n\n");
                reasoning.Append(delta.Text);
            }
            else
            {
                reply.Append(delta.Text);
            }

            thinking = delta.Reasoning;
        }
    }
}
