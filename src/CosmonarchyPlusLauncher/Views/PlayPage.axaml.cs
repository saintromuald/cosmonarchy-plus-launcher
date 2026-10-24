using Avalonia.Controls;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

public partial class PlayPage : UserControl
{
    public PlayPage()
    {
        InitializeComponent();
        DataContext = new PlayViewModel();
    }
}
