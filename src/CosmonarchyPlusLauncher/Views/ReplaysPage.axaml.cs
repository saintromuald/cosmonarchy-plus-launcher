using Avalonia.Controls;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

public partial class ReplaysPage : UserControl
{
    public ReplaysPage()
    {
        InitializeComponent();
        DataContext = new ReplaysViewModel();
    }
}
