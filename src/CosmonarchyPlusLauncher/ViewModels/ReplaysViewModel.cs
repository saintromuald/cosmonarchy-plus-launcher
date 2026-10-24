using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

public partial class ReplaysViewModel : ViewModelBase
{
    public const string AllPlayers = "All players";

    private const string DefaultRepoId = "release";
    private const int LatestCount = 20;
    private const int FollowedCount = 100;

    private readonly AppSettings _settings = AppRuntime.Settings;
    private readonly ReplayKrakenService _kraken = new();
    private IReadOnlyList<OnlineMatch> _allMatches = [];

    // ---- Online games -----------------------------------------------------

    public ObservableCollection<OnlineMatch> Matches { get; } = new();

    /// <summary>"All players" followed by the players the user follows.</summary>
    public ObservableCollection<string> Players { get; } = new();

    [ObservableProperty]
    private string _selectedPlayer = AllPlayers;

    [ObservableProperty]
    private OnlineMatch? _selectedMatch;

    [ObservableProperty]
    private string _newPlayer = "";

    [ObservableProperty]
    private string _onlineStatus = "Loading the latest games…";

    [ObservableProperty]
    private string _matchHint = "";

    [ObservableProperty]
    private bool _onlineBusy;

    public bool CanWatch => !OnlineBusy && SelectedMatch is not null;

    public bool CanUnfollow =>
        !OnlineBusy && !string.IsNullOrWhiteSpace(SelectedPlayer) && SelectedPlayer != AllPlayers;

    // ---- Local files ------------------------------------------------------

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
        RebuildPlayers();
        _ = LoadOnlineAsync(force: false);
    }

    partial void OnSelectedReplayChanged(string? value) => OnPropertyChanged(nameof(CanPlay));
    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanPlay));

    partial void OnSelectedPlayerChanged(string value) => ApplyFilter();

    partial void OnOnlineBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanWatch));
        OnPropertyChanged(nameof(CanUnfollow));
    }

    partial void OnSelectedMatchChanged(OnlineMatch? value)
    {
        OnPropertyChanged(nameof(CanWatch));
        MatchHint = value is null ? "" : $"Recorded on {value.VersionLabel}.";
        if (value is null)
            return;
        _ = Task.Run(() => DescribeVersionMatch(value));
    }

    // ---- Online commands --------------------------------------------------

    [RelayCommand]
    private Task RefreshOnlineAsync() => LoadOnlineAsync(force: true);

    [RelayCommand]
    private void FollowPlayer()
    {
        var name = NewPlayer.Trim();
        if (name.Length == 0)
            return;
        if (!_settings.FollowedPlayers.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.FollowedPlayers.Add(name);
            _settings.Save();
        }
        NewPlayer = "";
        RebuildPlayers();
        SelectedPlayer = _settings.FollowedPlayers
            .FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? AllPlayers;
        ApplyFilter();
    }

    [RelayCommand]
    private void UnfollowPlayer()
    {
        if (SelectedPlayer == AllPlayers)
            return;
        _settings.FollowedPlayers.RemoveAll(p =>
            p.Equals(SelectedPlayer, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        RebuildPlayers();
        SelectedPlayer = AllPlayers;
        ApplyFilter();
    }

    [RelayCommand]
    private async Task WatchSelectedAsync()
    {
        var match = SelectedMatch;
        if (match is null)
            return;
        OnlineBusy = true;
        try
        {
            var progress = new Progress<string>(s => OnlineStatus = s);
            var path = await _kraken.DownloadReplayAsync(match, ReplayRoot, progress);
            OnlineStatus = $"Launching {match.FileName}…";
            await PlayReplayFileAsync(path);
            OnlineStatus = $"Watching {match.FileName}.";
        }
        catch (Exception ex)
        {
            OnlineStatus = $"Could not watch: {ex.Message}";
        }
        finally
        {
            OnlineBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var match = SelectedMatch;
        if (match is null)
            return;
        OnlineBusy = true;
        try
        {
            var progress = new Progress<string>(s => OnlineStatus = s);
            var path = await _kraken.DownloadReplayAsync(match, ReplayRoot, progress);
            OnlineStatus = $"Saved {Path.GetFileName(path)} to {ReplayRoot}.";
            RefreshList();
        }
        catch (Exception ex)
        {
            OnlineStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            OnlineBusy = false;
        }
    }

    private async Task LoadOnlineAsync(bool force)
    {
        OnlineBusy = true;
        try
        {
            var progress = new Progress<string>(s => OnlineStatus = s);
            _allMatches = await _kraken.GetMatchesAsync(force, progress, CancellationToken.None);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            OnlineStatus = $"Could not load games: {ex.Message}";
        }
        finally
        {
            OnlineBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var player = SelectedPlayer == AllPlayers ? null : SelectedPlayer;
        var limit = player is null ? LatestCount : FollowedCount;
        var filtered = ReplayKrakenService.Filter(_allMatches, limit, player);
        Matches.Clear();
        foreach (var match in filtered)
            Matches.Add(match);
        SelectedMatch = Matches.FirstOrDefault();
        OnlineStatus = _allMatches.Count == 0
            ? "No games loaded."
            : player is null
                ? $"Latest {Matches.Count} of {_allMatches.Count} games."
                : $"{Matches.Count} recent game(s) for {player} ({_allMatches.Count} total).";
    }

    private void RebuildPlayers()
    {
        var selected = SelectedPlayer;
        Players.Clear();
        Players.Add(AllPlayers);
        foreach (var name in _settings.FollowedPlayers.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            Players.Add(name);
        SelectedPlayer = Players.Contains(selected) ? selected : AllPlayers;
    }

    /// <summary>Flag a build mismatch early: a replay only loads on the build
    /// it was recorded with. Compares against the selected installed version.</summary>
    private void DescribeVersionMatch(OnlineMatch match)
    {
        try
        {
            var version = AppRuntime.Versions.Selected;
            RepoEntry? repo;
            string? dir;
            if (version is not null && version.Repo is not null)
            {
                repo = version.Repo;
                dir = Path.Combine(_settings.RootPath, version.Dir);
            }
            else
            {
                repo = RepoCatalog.Find(_settings.SelectedRepoId) ?? RepoCatalog.Find(DefaultRepoId);
                dir = repo is null ? null : Path.Combine(_settings.RootPath, repo.BuildDir ?? "Release");
            }
            if (repo is null || dir is null)
                return;

            using var manager = new RepoManager(dir, repo.Url);
            var head = manager.NewestCommit()?.Sha;
            if (head is null)
                return;
            var hint = head[..8].Equals(match.GameVersion, StringComparison.OrdinalIgnoreCase)
                ? $"Recorded on {match.VersionLabel} — matches the installed build."
                : $"Recorded on {match.VersionLabel}; installed build is {head[..8]}. It may not load — update or pick a matching build first.";
            Avalonia.Threading.Dispatcher.UIThread.Post(() => MatchHint = hint);
        }
        catch
        {
            // A hint is best-effort; never surface an error for it.
        }
    }

    // ---- Local commands ---------------------------------------------------

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
        try
        {
            await PlayReplayFileAsync(replay);
            Status = $"Replay launched on {AppRuntime.Versions.Selected?.Name ?? "the installed build"}.";
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

    private static async Task PlayReplayFileAsync(string replay)
    {
        var version = AppRuntime.Versions.Selected;
        if (version is null)
            throw new InvalidOperationException("No version installed yet — install one on the Play tab.");
        var repo = version.Repo
                   ?? throw new InvalidOperationException($"Unknown repository '{version.RepoId}'.");
        var service = AppRuntime.CreateInstaller(repo, version);
        var progress = new Progress<string>(_ => { });
        await Task.Run(() => service.PlayReplayAsync(replay, progress, CancellationToken.None));
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
