using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

/// <summary>The dedicated Cosmonarchy Plus statistics tab: identical to the
/// Statistics tab but locked to the Plus channel. The channel selector hides
/// because the tab itself is the channel.</summary>
public partial class StatisticsPlusPage : UserControl
{
    public StatisticsPlusPage()
    {
        InitializeComponent();
        DataContext = new StatisticsViewModel(ChannelFilter.Plus);
    }

    /// <summary>Double-click on an opponent row opens the head-to-head
    /// history of every game between the active account and that opponent.</summary>
    private void OnOpponentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is StyledElement { DataContext: OpponentRow row } &&
            DataContext is StatisticsViewModel vm)
        {
            vm.OpenOpponent(row);
        }
    }
}
