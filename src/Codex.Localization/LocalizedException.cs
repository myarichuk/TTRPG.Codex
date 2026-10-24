namespace Codex.Localization;

/// <summary>
/// An error whose text is meant for the person using the app. It carries a resource key so the UI shows
/// it in their language (<see cref="Localizer.Describe"/>), while <see cref="Exception.Message"/> stays
/// English for logs.
/// </summary>
public class LocalizedException : Exception
{
    public LocalizedException(string key, params object?[] args)
        : base(Localizer.For("en").Get(key, args))
    {
        Key = key;
        Args = args;
    }

    public string Key { get; }

    public object?[] Args { get; }
}
