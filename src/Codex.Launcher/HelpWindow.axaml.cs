using Avalonia.Controls;
using Avalonia.Interactivity;
using static Codex.Launcher.Strings;

namespace Codex.Launcher;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        WindowText.Bind(this, Localize);
    }

    /// <summary>Builds the help. Every button or setting it mentions is looked up from the same key the launcher
    /// uses for that button, so the text cannot drift from the screen. The "1.  " and "•  " prefixes are
    /// locale-neutral and added here.</summary>
    private void Localize()
    {
        Title = L["Launcher.Help.Title"];
        CloseButton.Content = L["Launcher.Help.Got"];

        var lan = L["Launcher.Lan.Toggle"];
        var network = L["Launcher.Link.Network"];
        var tailscale = L["Launcher.Link.Tailscale"];
        var qr = L["Launcher.QrCode"];
        var copy = L["Launcher.CopyLink"];
        var check = L["Launcher.Tray.Check"];

        Body.Children.Clear();
        Add("title", L["Launcher.Help.Title"]);
        Add("body", L["Launcher.Help.Intro"]);

        Add("head", L["Launcher.Help.Head.Room"]);
        Add("body", Step(1, L["Launcher.Help.Room1", lan]));
        Add("body", Step(2, L["Launcher.Help.Room2", qr, network, copy]));
        Add("body", Step(3, L["Launcher.Help.Room3"]));
        Add("body", Step(4, L["Launcher.Help.Room4"]));

        Add("head", L["Launcher.Help.Head.Away"]);
        Add("body", L["Launcher.Help.AwayIntro"]);
        Add("body", Step(1, L["Launcher.Help.Away1"]));
        Add("body", Step(2, L["Launcher.Help.Away2"]));
        Add("body", Step(3, L["Launcher.Help.Away3", lan, tailscale]));

        Add("head", L["Launcher.Help.Head.Trouble"]);
        Add("body", Bullet(L["Launcher.Help.Trouble1"]));
        Add("body", Bullet(L["Launcher.Help.Trouble2"]));
        Add("body", Bullet(L["Launcher.Help.Trouble3", check]));
        Add("body", Bullet(L["Launcher.Help.Trouble4"]));
        Add("body", Bullet(L["Launcher.Help.Trouble5"]));

        Add("head", L["Launcher.Help.Head.Good"]);
        Add("body", Bullet(L["Launcher.Help.Good1", L["Launcher.Quit"]]));
        Add("body", Bullet(L["Launcher.Help.Good2", L["Launcher.CampaignData"], L["Launcher.Backups"]]));
        Add("body", Bullet(L["Launcher.Help.Good3"]));
        Add("body", Bullet(L["Launcher.Help.Good4", L["Launcher.Log"]]));
    }

    private static string Step(int number, string text) => $"{number}.  {text}";
    private static string Bullet(string text) => "•  " + text;

    private void Add(string style, string text)
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add(style);
        Body.Children.Add(block);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
