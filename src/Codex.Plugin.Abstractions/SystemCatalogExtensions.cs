namespace Codex.Plugin.Abstractions;

public static class SystemCatalogExtensions
{
    /// <summary>The system as the current user should read it: labels in their language where the system ships a translation,
    /// otherwise the definition itself. Use it for display; rules code takes <see cref="ISystemCatalog.GetDefinition"/>.</summary>
    public static SystemDefinition? GetLocalizedDefinition(this ISystemCatalog catalog, string systemId, string? culture = null) =>
        catalog.GetDefinition(systemId)?.Localize(culture ?? System.Globalization.CultureInfo.CurrentUICulture.Name);
}
