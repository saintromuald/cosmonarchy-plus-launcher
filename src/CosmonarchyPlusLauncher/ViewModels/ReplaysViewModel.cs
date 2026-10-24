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
    private const int SearchCount = 100;

    private readonly AppSettings _settings = AppRuntime.Settings;
    private readonly ReplayKrakenService _kraken = new();
    private readonly ReplayWatchService _watch;
    private readonly ReplayBuildResolver _resolver;

    private IReadOnlyList<OnlineMatch> _ogAll = [];
    private IReadOnlyList<OnlineMatch> _plusAll = [];

    public ReplaysViewModel()
    {
        _watch = new ReplayWatchService(_settings, _kraken);
        _resolver = new ReplayBuildResolver(_settings);

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

    // ---- Shared follow list ----------------------------------------------

    public ObservableCollection<string> Players { get; } = new();

    [ObservableProperty]
    private string _newPlayer = "";

    // ---- Cosmonarchy OG tab ----------------------------------------------

    public ObservableCollection<OnlineMatch> OgMatches { get; } = new();

    [ObservableProperty]
    private string _ogSearch = "";

    [ObservableProperty]
    private string _ogPlayer = AllPlayers;

    [ObservableProperty]
    private OnlineMatch? _selectedOgMatch;

    [ObservableProperty]
    private string _ogStatus = "Loading the latest games…";

    [ObservableProperty]
    private string _ogHint = "";

    [ObservableProperty]
    private bool _ogBusy;

    public bool CanWatchOg => !OgBusy && SelectedOgMatch is not null;

    public bool CanUnfollowOg => !OgBusy && OgPlayer != AllPlayers;

    // ---- Cosmonarchy Plus tab --------------------------------------------

    public ObservableCollection<OnlineMatch> PlusMatches { get; } = new();

    [ObservableProperty]
    private string _plusSearch = "";

    [ObservableProperty]
    private string _plusPlayer = AllPlayers;

    [ObservableProperty]
    private OnlineMatch? _selectedPlusMatch;

    [ObservableProperty]
    private string _plusStatus = "Loading the latest games…";

    [ObservableProperty]
    private string _plusHint = "";

    [ObservableProperty]
    private bool _plusBusy;

    public bool CanWatchPlus => !PlusBusy && SelectedPlusMatch is not null;

    public bool CanUnfollowPlus => !PlusBusy && PlusPlayer != AllPlayers;

    // ---- Local files -----------------------------------------------------

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

    partial void OnSelectedReplayChanged(string? value) => OnPropertyChanged(nameof(CanPlay));
    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanPlay));

    partial void OnOgSearchChanged(string value) => ApplyOgFilter();

    partial void OnPlusSearchChanged(string value) => ApplyPlusFilter();

    partial void OnOgPlayerChanged(string value) => ApplyOgFilter();

    partial void OnPlusPlayerChanged(string value) => ApplyPlusFilter();

    partial void OnOgBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanWatchOg));
        OnPropertyChanged(nameof(CanUnfollowOg));
    }

    partial void OnPlusBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanWatchPlus));
        OnPropertyChanged(nameof(CanUnfollowPlus));
    }

    partial void OnSelectedOgMatchChanged(OnlineMatch? value)
    {
        OnPropertyChanged(nameof(CanWatchOg));
        OgHint = value is null ? "" : Hint(value, ReplayChannel.Original);
    }

    partial void OnSelectedPlusMatchChanged(OnlineMatch? value)
    {
        OnPropertyChanged(nameof(CanWatchPlus));
        PlusHint = value is null ? "" : Hint(value, ReplayChannel.Plus);
    }

    // ---- Online commands -------------------------------------------------

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
        ApplyOgFilter();
        ApplyPlusFilter();
    }

    [RelayCommand]
    private void UnfollowOg() => Unfollow(OgPlayer);

    [RelayCommand]
    private void UnfollowPlus() => Unfollow(PlusPlayer);

    private void Unfollow(string player)
    {
        if (player == AllPlayers)
            return;
        _settings.FollowedPlayers.RemoveAll(p => p.Equals(player, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        RebuildPlayers();
        ApplyOgFilter();
        ApplyPlusFilter();
    }

    [RelayCommand]
    private async Task WatchOgAsync() => await WatchAsync(SelectedOgMatch, isPlus: false);

    [RelayCommand]
    private async Task WatchPlusAsync() => await WatchAsync(SelectedPlusMatch, isPlus: true);

    [RelayCommand]
    private async Task DownloadOgAsync() => await DownloadOnlyAsync(SelectedOgMatch, isPlus: false);

    [RelayCommand]
    private async Task DownloadPlusAsync() => await DownloadOnlyAsync(SelectedPlusMatch, isPlus: true);

    private async Task WatchAsync(OnlineMatch? match, bool isPlus)
    {
        if (match is null)
            return;
        SetBusy(isPlus, true);
        try
        {
            var progress = new Progress<string>(s => SetStatus(isPlus, s));
            SetStatus(isPlus, "Working out the build…");
            var build = await Task.Run(() => _watch.WatchAsync(match, progress, CancellationToken.None));
            SetStatus(isPlus, $"Launched {match.FileName} on {build.Label} {build.FullSha[..8]}.");
        }
        catch (Exception ex)
        {
            SetStatus(isPlus, $"Could not watch: {ex.Message}");
        }
        finally
        {
            SetBusy(isPlus, false);
        }
    }

    private async Task DownloadOnlyAsync(OnlineMatch? match, bool isPlus)
    {
        if (match is null)
            return;
        SetBusy(isPlus, true);
        try
        {
            var progress = new Progress<string>(s => SetStatus(isPlus, s));
            var path = await _kraken.DownloadReplayAsync(match, ReplayRoot, progress);
            SetStatus(isPlus, $"Saved {Path.GetFileName(path)} to {ReplayRoot}.");
            RefreshList();
        }
        catch (Exception ex)
        {
            SetStatus(isPlus, $"Download failed: {ex.Message}");
        }
        finally
        {
            SetBusy(isPlus, false);
        }
    }

    private async Task LoadOnlineAsync(bool force)
    {
        OgBusy = true;
        PlusBusy = true;
        try
        {
            var progress = new Progress<string>(s => OgStatus = s);
            var all = await _kraken.GetMatchesAsync(force, progress, CancellationToken.None);

            // Tell the maintainer's builds apart from upstream: a short SHA
            // reachable from the fork remote is a Plus build.
            var plusRelease = ForkShas(prerelease: false);
            var plusPrerelease = ForkShas(prerelease: true);
            _ogAll = [];
            _plusAll = [];
            var og = new List<OnlineMatch>();
            var plus = new List<OnlineMatch>();
            foreach (var match in all)
            {
                var shas = ReplayBuildResolver.IsPrerelease(match.GameVersionKind)
                    ? plusPrerelease
                    : plusRelease;
                var isPlus = shas.Any(s => s.StartsWith(match.GameVersion, StringComparison.OrdinalIgnoreCase));
                (isPlus ? plus : og).Add(match);
            }
            _ogAll = og;
            _plusAll = plus;

            ApplyOgFilter();
            ApplyPlusFilter();
            if (_plusAll.Count == 0)
                PlusStatus = "No Cosmonarchy Plus games found yet — play a Plus build online and it appears here.";
        }
        catch (Exception ex)
        {
            OgStatus = $"Could not load games: {ex.Message}";
            PlusStatus = OgStatus;
        }
        finally
        {
            OgBusy = false;
            PlusBusy = false;
        }
    }

    private IReadOnlySet<string> ForkShas(bool prerelease)
    {
        var fork = ReplayBuildResolver.ForkRepo(prerelease);
        var dir = Path.Combine(_settings.RootPath, prerelease ? "Prerelease" : "Release");
        if (fork is null)
            return new HashSet<string>();
        try
        {
            return _resolver.ForkShas(fork, dir);
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private void ApplyOgFilter() => ApplyFilter(_ogAll, OgMatches, OgPlayer, OgSearch, false);
    private void ApplyPlusFilter() => ApplyFilter(_plusAll, PlusMatches, PlusPlayer, PlusSearch, true);

    private void ApplyFilter(
        IReadOnlyList<OnlineMatch> source, ObservableCollection<OnlineMatch> target,
        string player, string search, bool isPlus)
    {
        var query = player == AllPlayers ? null : player;
        var text = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        IEnumerable<OnlineMatch> matches = source;
        if (query is not null)
            matches = matches.Where(m => MatchHasPlayer(m, query));
        if (text is not null)
            matches = matches.Where(m => MatchContains(m, text));

        var limit = query is not null || text is not null ? SearchCount : LatestCount;
        var list = matches.OrderByDescending(m => m.UploadedAt).Take(limit).ToList();

        target.Clear();
        foreach (var match in list)
            target.Add(match);
        if (isPlus)
            SelectedPlusMatch = target.FirstOrDefault();
        else
            SelectedOgMatch = target.FirstOrDefault();

        var channel = isPlus ? "Cosmonarchy Plus" : "Cosmonarchy OG";
        var shown = $"{list.Count} shown";
        var total = $"{source.Count} {channel} games";
        var filter = query is not null ? $" · {query}" : text is not null ? $" · \"{text}\"" : "";
        SetStatus(isPlus, $"{shown} · {total}{filter}.");
    }

    private static bool MatchHasPlayer(OnlineMatch match, string player) =>
        match.UploaderName.Equals(player, StringComparison.OrdinalIgnoreCase) ||
        match.Players.Any(p => p.Name.Equals(player, StringComparison.OrdinalIgnoreCase));

    private static bool MatchContains(OnlineMatch match, string text)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        return match.PlayersLabel.Contains(text, comparison) ||
               match.UploaderName.Contains(text, comparison) ||
               match.MapName.Contains(text, comparison) ||
               match.VersionLabel.Contains(text, comparison);
    }

    private static string Hint(OnlineMatch match, ReplayChannel channel)
    {
        var label = channel == ReplayChannel.Plus ? "Cosmonarchy Plus" : "Cosmonarchy OG";
        return $"Recorded on {match.VersionLabel} — Watch switches to the {label} build automatically.";
    }

    private void SetStatus(bool isPlus, string text)
    {
        if (isPlus)
            PlusStatus = text;
        else
            OgStatus = text;
    }

    private void SetBusy(bool isPlus, bool busy)
    {
        if (isPlus)
            PlusBusy = busy;
        else
            OgBusy = busy;
    }

    private void RebuildPlayers()
    {
        var og = OgPlayer;
        var plus = PlusPlayer;
        Players.Clear();
        Players.Add(AllPlayers);
        foreach (var name in _settings.FollowedPlayers.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            Players.Add(name);
        OgPlayer = Players.Contains(og) ? og : AllPlayers;
        PlusPlayer = Players.Contains(plus) ? plus : AllPlayers;
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

    private static async Task PlayReplayFileAsync(string replay)
    {
        var version = AppRuntime.Versions.Selected
                      ?? throw new InvalidOperationException("No installed version — install one on the Play page first.");
        var repo = RepoCatalog.Find(version.RepoId)
                   ?? RepoCatalog.Find(DefaultRepoId)
                   ?? throw new InvalidOperationException("No repository configured.");
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
