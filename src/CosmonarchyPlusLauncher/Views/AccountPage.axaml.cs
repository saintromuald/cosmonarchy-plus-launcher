using Avalonia.Controls;
using CosmonarchyPlusLauncher.ViewModels;

namespace CosmonarchyPlusLauncher.Views;

public partial class AccountPage : UserControl
{
    public AccountPage()
    {
        InitializeComponent();
        DataContext = new AccountViewModel();
    }
}
