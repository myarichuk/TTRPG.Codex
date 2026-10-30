namespace Codex.Web.Components.Shared;

/// <summary>
/// Lets one save of an editor run at a time. A second press of Save while the first is still being written (a double click,
/// Ctrl+S pressed twice) does nothing, rather than saving the same text twice or, for something new, making it twice.
/// </summary>
public sealed class SaveGate
{
    private bool busy;

    public bool IsBusy => busy;

    public async Task RunAsync(Func<Task> save)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        try
        {
            await save();
        }
        finally
        {
            busy = false;
        }
    }
}
