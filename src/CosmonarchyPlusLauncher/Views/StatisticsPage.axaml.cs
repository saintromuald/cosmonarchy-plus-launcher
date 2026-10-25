using Avalonia.Controls;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

public partial class StatisticsPage : UserControl
{
    public StatisticsPage()
    {
        InitializeComponent();
        DataContext = new StatisticsViewModel();
    }
}
