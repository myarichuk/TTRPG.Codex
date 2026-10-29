using Microsoft.AspNetCore.Components;

namespace Codex.Web.Services;

public static class RichText
{
    /// <summary>Renders a localized sentence in which `backtick` segments are code samples.</summary>
    public static RenderFragment Code(string text) => builder =>
    {
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            if (i % 2 == 1)
            {
                builder.OpenElement(0, "code");
                builder.AddContent(1, parts[i]);
                builder.CloseElement();
            }
            else
            {
                builder.AddContent(2, parts[i]);
            }
        }
    };
}
