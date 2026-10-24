using CommunityToolkit.Mvvm.ComponentModel;

namespace CosmonarchyPlusLauncher.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "Welcome to the Cosmonarchy Plus Launcher!";
}
