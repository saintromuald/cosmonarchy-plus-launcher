using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

public partial class FavouritesPage : UserControl
{
    public FavouritesPage()
    {
        InitializeComponent();
        DataContext = new FavouritesViewModel();
    }

    private async void OnImportFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FavouritesViewModel vm)
            return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a Cosmonarchy share",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Cosmonarchy share")
                {
                    Patterns = ["*.json"],
                    MimeTypes = ["application/json"],
                },
            ],
        });
        if (files.Count != 1)
            return;
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        vm.Import(await reader.ReadToEndAsync(), files[0].Name);
    }
}
