using CommunityToolkit.Mvvm.ComponentModel;
using Codex.Plugin.Abstractions;

namespace Codex.Authoring.ViewModels;

public partial class EditPackDialogViewModel : ObservableObject
{
    private readonly PackManifest _originalManifest;

    [ObservableProperty]
    private string _packName = string.Empty;

    [ObservableProperty]
    private string _packDescription = string.Empty;

    [ObservableProperty]
    private string _thumbnailPath = string.Empty;

    [ObservableProperty]
    private string _systemId = string.Empty; // Read-only

    public EditPackDialogViewModel(PackManifest original)
    {
        _originalManifest = original;
        PackName = original.Name;
        PackDescription = original.Description ?? string.Empty;
        ThumbnailPath = original.ThumbnailPath ?? string.Empty;
        SystemId = original.SystemId;
    }

    public PackManifest UpdateManifest()
    {
        return _originalManifest with
        {
            Name = PackName,
            Description = PackDescription,
            ThumbnailPath = ThumbnailPath
        };
    }
}
