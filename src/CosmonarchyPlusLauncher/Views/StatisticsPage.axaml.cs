using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

public partial class StatisticsPage : UserControl
{
    public StatisticsPage()
    {
        InitializeComponent();
        DataContext = new StatisticsViewModel();
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
