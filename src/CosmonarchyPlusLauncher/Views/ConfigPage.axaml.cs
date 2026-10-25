using Avalonia.Controls;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

public partial class ConfigPage : UserControl
{
    public ConfigPage()
    {
        InitializeComponent();
        DataContext = new ConfigViewModel();
    }
}
