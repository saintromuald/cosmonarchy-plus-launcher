using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

public partial class ReplaysPage : UserControl
{
    public ReplaysPage()
    {
        InitializeComponent();
        DataContext = new ReplaysViewModel();
    }

    /// <summary>Double-click a game row: watch it immediately (the right
    /// build is checked out, the replay downloads, the game launches
    /// fullscreen and the menus are driven to start playback).</summary>
    private void OnReplayDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ReplaysViewModel vm)
            return;
        if (sender is not Control { Tag: string channel, DataContext: var row })
            return;
        switch (channel, row)
        {
            case ("og", ReplayRow og):
                vm.WatchRow(og, isPlus: false);
                break;
            case ("plus", ReplayRow plus):
                vm.WatchRow(plus, isPlus: true);
                break;
            case ("followed", FollowedReplayRow followed):
                vm.WatchFollowedRow(followed);
                break;
        }
    }
}
