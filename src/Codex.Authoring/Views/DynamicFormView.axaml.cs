using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Codex.Authoring.Views;

public partial class DynamicFormView : UserControl
{
    public DynamicFormView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}