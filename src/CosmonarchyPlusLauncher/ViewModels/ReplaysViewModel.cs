using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

public partial class ReplaysViewModel : ViewModelBase
{
    public ObservableCollection<string> Replays { get; } = new();

    [ObservableProperty]
    private string? _selectedReplay;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _replayRoot = "";

    [ObservableProperty]
    private bool _busy;

    public bool CanPlay => !Busy && SelectedReplay is not null;

    public ReplaysViewModel()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "Games", "cosmonarchy", "replays"),
            Path.Combine(AppRuntime.Settings.RootPath, "Starcraft", "maps", "replays"),
            Path.Combine(AppRuntime.Settings.RootPath, "Starcraft", "Maps", "replays"),
        };
        _replayRoot = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
        RefreshList();
    }

    partial void OnSelectedReplayChanged(string? value) => OnPropertyChanged(nameof(CanPlay));
    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanPlay));

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

    [RelayCommand]
    private async Task PlaySelectedAsync()
    {
        var replay = SelectedReplay;
        if (replay is null || !File.Exists(replay))
            return;

        Busy = true;
        Status = "Launching replay…";
        var progress = new Progress<string>(s => Status = s);
        try
        {
            var repo = RepoCatalog.Find(AppRuntime.Settings.SelectedRepoId)
                       ?? RepoCatalog.Find(AppSettings_DefaultRepoId)
                       ?? throw new InvalidOperationException("No repository configured.");
            var service = AppRuntime.CreateInstaller(repo);
            await Task.Run(() => service.PlayReplayAsync(replay, progress, CancellationToken.None));
            Status = "Replay launched.";
        }
        catch (Exception ex)
        {
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private const string AppSettings_DefaultRepoId = "release";

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
