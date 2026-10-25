using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using Codex.Localization;

namespace Codex.Launcher;

/// <summary>The launcher's current language. <see cref="L"/> is set once before any window exists
/// (<see cref="Use"/>) and swapped at runtime by <see cref="Set"/>, which tells open windows to relabel.
/// Call sites index <c>L</c> with a literal key (with <c>using static Codex.Launcher.Strings;</c>) so the
/// key-existence test can see them.</summary>
internal static class Strings
{
    public static Localizer L { get; private set; } = Create(SupportedCultures.Default);

    /// <summary>The active language tag: <c>en</c>, <c>ru</c> or <c>he</c>.</summary>
    public static string Tag { get; private set; } = SupportedCultures.Default;

    public static bool IsRtl => SupportedCultures.IsRtl(Tag);

    /// <summary>Raised on the UI thread after the language changed at runtime.</summary>
    public static event Action? Changed;

    /// <summary>The language a stored setting resolves to: the setting itself, or the operating system's when unset.</summary>
    public static string Effective(string? stored) =>
        SupportedCultures.IsSupported(stored) ? SupportedCultures.Normalize(stored) : OsLanguage.Detect();

    /// <summary>Picks the language at startup, before any window exists (no change notification).</summary>
    public static void Use(string? stored)
    {
        Tag = Effective(stored);
        L = Create(Tag);
    }

    /// <summary>Switches language at runtime and tells open windows and the tray menu to relabel.</summary>
    public static void Set(string tag)
    {
        var normalized = SupportedCultures.Normalize(tag);
        if (normalized == Tag) return;
        Tag = normalized;
        L = Create(Tag);
        Changed?.Invoke();
    }

    public static FlowDirection Flow => IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Keeps an address or other left-to-right run in one piece inside right-to-left text.</summary>
    public static string Isolate(string text) => IsRtl ? "⁦" + text + "⁩" : text;

    public static string LinkLabel(LinkKind kind) => kind switch
    {
        LinkKind.Network => L["Launcher.Link.Network"],
        LinkKind.Tailscale => L["Launcher.Link.Tailscale"],
        _ => L["Launcher.Link.This"],
    };

    private static Localizer Create(string tag)
    {
        try
        {
            return Localizer.For(tag);
        }
        catch (CultureNotFoundException)
        {
            return new Localizer(); // no culture data at all: English from the neutral resources
        }
    }
}

/// <summary>Window plumbing shared by every launcher window.</summary>
internal static class WindowText
{
    /// <summary>Applies the language now and again whenever it changes while the window is open:
    /// flow direction (right-to-left for Hebrew) first, then the window's own relabelling.</summary>
    public static void Bind(Window window, Action localize)
    {
        void Apply()
        {
            window.FlowDirection = Strings.Flow;
            localize();
        }

        Apply();
        Strings.Changed += Apply;
        window.Closed += (_, _) => Strings.Changed -= Apply;
    }
}

/// <summary>The language drop-down used on the welcome and main windows (native names, never translated).</summary>
internal static class LanguagePicker
{
    public static void Sync(ComboBox box)
    {
        box.ItemsSource ??= SupportedCultures.All.Select(c => c.NativeName).ToList();
        var index = -1;
        for (var i = 0; i < SupportedCultures.All.Count; i++)
        {
            if (SupportedCultures.All[i].Tag == Strings.Tag) index = i;
        }

        box.SelectedIndex = index;
    }

    public static string? TagAt(ComboBox box) =>
        box.SelectedIndex >= 0 && box.SelectedIndex < SupportedCultures.All.Count ? SupportedCultures.All[box.SelectedIndex].Tag : null;
}

/// <summary>The operating system's display language. The launcher runs with invariant globalization, so
/// <see cref="CultureInfo.CurrentUICulture"/> is always empty and the platform has to be asked directly.</summary>
internal static class OsLanguage
{
    public static string Detect()
    {
        foreach (var candidate in Candidates())
        {
            var language = candidate.Trim().Split('-', '_', '.', '@')[0].ToLowerInvariant();
            if (language == "iw") language = "he";
            if (SupportedCultures.IsSupported(language)) return language;
        }

        return SupportedCultures.Default;
    }

    private static List<string> Candidates()
    {
        var found = new List<string>();
        try
        {
            if (CultureInfo.CurrentUICulture.Name.Length > 0) found.Add(CultureInfo.CurrentUICulture.Name);
            if (OperatingSystem.IsWindows())
            {
                found.Add((GetUserDefaultUILanguage() & 0x3FF) switch { 0x19 => "ru", 0x0D => "he", _ => "en" });
            }
            else
            {
                if (OperatingSystem.IsMacOS()) found.InsertRange(0, MacLanguages());
                foreach (var name in new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" })
                {
                    if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) found.AddRange(value.Split(':'));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            // Not knowing the language is fine: English.
        }

        return found;
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>The user's preferred languages, in order (a GUI app on macOS does not see LANG).</summary>
    private static IEnumerable<string> MacLanguages()
    {
        var info = new ProcessStartInfo("/usr/bin/defaults") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("read");
        info.ArgumentList.Add("-g");
        info.ArgumentList.Add("AppleLanguages");
        using var process = Process.Start(info);
        if (process == null) return [];
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(2000);
        return Regex.Matches(text, "[A-Za-z]{2,3}(?:-[A-Za-z0-9]+)*").Select(m => m.Value).ToList();
    }
}
