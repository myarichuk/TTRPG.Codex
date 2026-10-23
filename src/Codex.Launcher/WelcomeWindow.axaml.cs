using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using static Codex.Launcher.Strings;

namespace Codex.Launcher;

public partial class WelcomeWindow : Window
{
    public const string Host = "host";
    public const string Solo = "solo";

    private readonly LauncherViewModel? model;
    private bool loading = true;

    public WelcomeWindow() : this(null) { }

    /// <summary>With a model, the language drop-down is saved to launcher.json; without one it only changes the display.</summary>
    internal WelcomeWindow(LauncherViewModel? model)
    {
        this.model = model;
        InitializeComponent();
        WindowText.Bind(this, Localize);
        loading = false;
    }

    private void Localize()
    {
        var wasLoading = loading;
        loading = true;
        LanguagePicker.Sync(LanguageBox);
        loading = wasLoading;

        Title = L["Launcher.Welcome.Title"];
        TitleText.Text = L["Launcher.Welcome.Title"];
        IntroText.Text = L["Launcher.Welcome.Intro"];
        HostTitle.Text = L["Launcher.Welcome.HostTitle"];
        HostBody.Text = L["Launcher.Welcome.HostBody"];
        SoloTitle.Text = L["Launcher.Welcome.SoloTitle"];
        SoloBody.Text = L["Launcher.Welcome.SoloBody"];
        FooterText.Text = L["Launcher.Welcome.Footer"];
        ToolTip.SetTip(LanguageBox, L["Launcher.Language"]);
        AutomationProperties.SetName(LanguageBox, L["Launcher.Language"]);
    }

    private void OnLanguageSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || LanguagePicker.TagAt(LanguageBox) is not { } tag || tag == Strings.Tag) return;
        if (model != null) model.SetLanguage(tag);
        else Strings.Set(tag);
    }

    private void OnHost(object? sender, RoutedEventArgs e) => Close(Host);
    private void OnSolo(object? sender, RoutedEventArgs e) => Close(Solo);
}
