using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Codex.Plugin.Abstractions;
using Codex.Systems.DnD5e;
using Codex.Systems.SWFFG;

namespace Codex.Authoring.ViewModels;

public partial class NewPackDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _packName = string.Empty;

    [ObservableProperty]
    private string _packDescription = string.Empty;

    [ObservableProperty]
    private string _thumbnailPath = string.Empty;

    [ObservableProperty]
    private SystemItemViewModel? _selectedSystem;

    public ObservableCollection<SystemItemViewModel> AvailableSystems { get; } = new();

    public NewPackDialogViewModel()
    {
        // For the authoring app, we can mock or statically load available systems
        // to present in the dialog since we are just scaffolding a wizard right now.
        var systems = new ICodexSystemPlugin[]
        {
            new DnD5ePlugin(),
            new SwffgPlugin()
        };

        foreach (var system in systems)
        {
            AvailableSystems.Add(new SystemItemViewModel(system));
        }
    }

    public PackManifest? CreatePackManifest()
    {
        if (string.IsNullOrWhiteSpace(PackName) || SelectedSystem == null)
        {
            return null; // Can't create without a name and system
        }

        return new PackManifest(
            Id: System.Guid.NewGuid().ToString(),
            Name: PackName,
            Version: "0.1.0",
            SystemId: SelectedSystem.SystemId,
            Description: PackDescription,
            ThumbnailPath: ThumbnailPath
        );
    }
}

public partial class SystemItemViewModel : ObservableObject
{
    public string SystemId { get; }
    public string Name { get; }
    public string Description { get; }
    public string ThumbnailPath { get; }

    public SystemItemViewModel(ICodexSystemPlugin plugin)
    {
        SystemId = plugin.SystemId;
        Name = plugin.Name;
        Description = plugin.Description;
        ThumbnailPath = plugin.ThumbnailPath;
    }
}
