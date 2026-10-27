using Codex.Core.AI;
using Codex.Localization;

namespace Codex.Web.Services;

/// <summary>What an AI button shows when its call failed: a sentence in the reader's language saying what to do about it
/// (check the key, wait, pick a faster model), not the SDK's stack of wrapped retries. The page puts its own lead-in before it
/// ("The AI couldn't answer: …").</summary>
public static class AiFailureText
{
    public static string For(Localizer l, Exception ex)
    {
        if (ex is LocalizedException)
        {
            return l.Describe(ex);
        }

        var failure = AiFailure.From(ex);
        return failure.Kind switch
        {
            AiFailureKind.Timeout => l["Common.Ai.Problem.Timeout"],
            AiFailureKind.Auth => l["Common.Ai.Problem.Auth"],
            AiFailureKind.NotFound => l["Common.Ai.Problem.NotFound"],
            AiFailureKind.RateLimit => l["Common.Ai.Problem.RateLimit"],
            AiFailureKind.Server => l["Common.Ai.Problem.Server"],
            AiFailureKind.Network => l["Common.Ai.Problem.Network"],
            _ => failure.Detail
        };
    }
}
