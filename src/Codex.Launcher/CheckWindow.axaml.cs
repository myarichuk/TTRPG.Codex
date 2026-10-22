using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Codex.Launcher;

public partial class CheckWindow : Window
{
    private readonly LauncherViewModel? model;

    public CheckWindow() => InitializeComponent();

    internal CheckWindow(LauncherViewModel model) : this()
    {
        this.model = model;
        Opened += async (_, _) => await RunAsync();
    }

    internal void Show(IReadOnlyList<CheckResult> results)
    {
        Results.Children.Clear();
        foreach (var result in results)
        {
            var dot = new Ellipse { Width = 12, Height = 12, Margin = new Avalonia.Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top, Fill = new SolidColorBrush(Color.Parse(result.Status switch
            {
                CheckStatus.Ok => "#5fbf6a",
                CheckStatus.Warn => "#e0b64a",
                CheckStatus.Fail => "#d9604a",
                _ => "#7f8fa6",
            })) };
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = result.Title, FontWeight = FontWeight.Bold, FontSize = 15, Foreground = new SolidColorBrush(Color.Parse("#f3e9d2")), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new SelectableTextBlock { Text = result.Detail, FontSize = 13, LineHeight = 19, Foreground = new SolidColorBrush(Color.Parse("#cbbfa5")), TextWrapping = TextWrapping.Wrap });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(dot);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            Results.Children.Add(row);
        }

        var problems = results.Count(r => r.Status == CheckStatus.Fail);
        var warnings = results.Count(r => r.Status == CheckStatus.Warn);
        Summary.Text = problems > 0 ? "Found something to fix. Start at the first red item."
            : warnings > 0 ? "Nothing is broken, but check the yellow items."
            : "Everything I can check looks fine. If a player still cannot connect, see the last item.";
    }

    private async Task RunAsync()
    {
        if (model == null) return;
        Summary.Text = "Checking…";
        var results = await JoinCheck.RunAsync(model.Settings, model.Controller.State, model.Controller.Problem,
            LanAddress.All(), LanAddress.Tailnet(), JoinCheck.ReachesAsync, JoinCheck.PortIsFree);
        Show(results);
    }

    private async void OnAgain(object? sender, RoutedEventArgs e) => await RunAsync();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
