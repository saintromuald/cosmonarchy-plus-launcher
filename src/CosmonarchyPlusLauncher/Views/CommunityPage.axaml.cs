using Avalonia.Controls;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

public partial class CommunityPage : UserControl
{
    public CommunityPage()
    {
        InitializeComponent();
        DataContext = new CommunityViewModel();
    }
}
