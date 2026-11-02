namespace Codex.Web.Services;

/// <summary>A status message and its tone, rendered by <c>StatusMessage</c> as a live region. <see cref="Undo"/>, when set,
/// puts back what the message reports (U3): the toast shows an Undo button that runs it.</summary>
public sealed record Flash(string Text, FlashTone Tone = FlashTone.Info, Func<Task>? Undo = null)
{
    public static Flash Info(string text) => new(text);
    public static Flash Success(string text) => new(text, FlashTone.Success);
    public static Flash Error(string text) => new(text, FlashTone.Error);

    /// <summary>A change that is made, with the way to put it back.</summary>
    public static Flash Undoable(string text, Func<Task> undo) => new(text, FlashTone.Success, undo);
}

public enum FlashTone
{
    Info,
    Success,
    Error
}
