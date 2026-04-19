namespace Codex.Web;

public static class UrlHelper
{
    /// <summary>
    /// Returns true if the URL is local to the host.
    /// A local URL starts with '/' but not '//' or '/\'.
    /// </summary>
    public static bool IsLocalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        // Allow "/"
        if (url.Length == 1 && url[0] == '/')
        {
            return true;
        }

        // Must start with '/' and not be followed by another '/' or '\'
        if (url[0] == '/' && url[1] != '/' && url[1] != '\\')
        {
            return true;
        }

        return false;
    }
}
