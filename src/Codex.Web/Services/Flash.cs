namespace Codex.Web.Services;

/// <summary>A status message and its tone, rendered by <c>StatusMessage</c> as a live region.</summary>
public sealed record Flash(string Text, FlashTone Tone = FlashTone.Info)
{
    public static Flash Info(string text) => new(text);
    public static Flash Success(string text) => new(text, FlashTone.Success);
    public static Flash Error(string text) => new(text, FlashTone.Error);
}

public enum FlashTone
{
    Info,
    Success,
    Error
}
