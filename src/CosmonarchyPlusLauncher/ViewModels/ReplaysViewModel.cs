using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

public partial class ReplaysViewModel : ViewModelBase
{
    public ObservableCollection<string> Replays { get; } = new();

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _replayRoot = "";

    public ReplaysViewModel()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "Games", "cosmonarchy", "replays"),
            Path.Combine(AppRuntime.Settings.RootPath, "Starcraft", "maps", "replays"),
            Path.Combine(AppRuntime.Settings.RootPath, "Starcraft", "Replays"),
        };
        _replayRoot = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
        RefreshList();
    }

    [RelayCommand]
    private void Refresh()
    {
        RefreshList();
        Status = $"Found {Replays.Count} replay file(s).";
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!Directory.Exists(ReplayRoot))
            Directory.CreateDirectory(ReplayRoot);
        Process.Start(new ProcessStartInfo(ReplayRoot) { UseShellExecute = true });
    }

    private void RefreshList()
    {
        Replays.Clear();
        if (!Directory.Exists(ReplayRoot))
            return;
        foreach (var file in Directory.GetFiles(ReplayRoot, "*.rep", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(500))
            Replays.Add(file);
    }
}
