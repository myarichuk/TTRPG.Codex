using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;

namespace Codex.Authoring.Controls;

public partial class ImagePickerControl : UserControl
{
    public static readonly DirectProperty<ImagePickerControl, string?> ImagePathProperty =
        AvaloniaProperty.RegisterDirect<ImagePickerControl, string?>(
            nameof(ImagePath),
            o => o.ImagePath,
            (o, v) => o.ImagePath = v);

    private string? _imagePath;

    public string? ImagePath
    {
        get => _imagePath;
        set
        {
            SetAndRaise(ImagePathProperty, ref _imagePath, value);
            UpdateImageSource();
        }
    }

    public static readonly DirectProperty<ImagePickerControl, Bitmap?> ImageSourceProperty =
        AvaloniaProperty.RegisterDirect<ImagePickerControl, Bitmap?>(
            nameof(ImageSource),
            o => o.ImageSource,
            (o, v) => o.ImageSource = v);

    private Bitmap? _imageSource;

    public Bitmap? ImageSource
    {
        get => _imageSource;
        private set => SetAndRaise(ImageSourceProperty, ref _imageSource, value);
    }

    public ImagePickerControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Select Image",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp" }
                    }
                }
            });

            if (files.Count > 0)
            {
                ImagePath = files[0].Path.LocalPath;
            }
        }
    }

    private void UpdateImageSource()
    {
        if (string.IsNullOrEmpty(ImagePath))
        {
            ImageSource = null;
            return;
        }

        try
        {
            ImageSource = new Bitmap(ImagePath);
        }
        catch (Exception)
        {
            ImageSource = null;
        }
    }
}
