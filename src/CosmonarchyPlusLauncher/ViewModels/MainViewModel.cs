using CommunityToolkit.Mvvm.ComponentModel;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>A selectable version in the play page: Latest (fetch + reset to
/// the remote branch), Current (keep the installed checkout), or a specific
/// commit from the log.</summary>
public sealed record VersionOption(string Label, string? Sha);

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "Cosmonarchy Plus Launcher";
}
