using Avalonia.Controls;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }
}
