using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>One online game plus this machine's library state (favourite,
/// watched, note) for the list badges.</summary>
public sealed record ReplayRow(OnlineMatch Match, bool Favorite, bool Watched, bool HasNote)
{
    public string PlayersLabel => Match.PlayersLabel;
    public string DateLabel => Match.DateLabel;
    public string MapName => Match.MapName;
    public string DurationLabel => Match.DurationLabel;
    public string VersionLabel => Match.VersionLabel;

    public string Badges => string.Concat(
        Favorite ? "★ " : "",
        Watched ? "▶ " : "",
        HasNote ? "✎ " : "");
}

/// <summary>A checkbox in the race-vs-race matrix.</summary>
public partial class RacePairOption : ObservableObject
{
    public RacePairOption(string raceA, string raceB)
    {
        Key = ReplayQuery.PairKey(raceA, raceB);
        Label = $"{Short(raceA)}v{Short(raceB)}";
    }

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public static string Short(string race) => race switch
    {
        "Terran" => "T",
        "Protoss" => "P",
        "Zerg" => "Z",
        "Askosi" => "A",
        _ => "O",
    };
}

public partial class ReplaysViewModel : ViewModelBase
{
    public const string AllPlayers = "All players";

    private const int LatestCount = 20;
    private const int FilteredCount = 100;

    private readonly AppSettings _settings = AppRuntime.Settings;
    private readonly ReplayKrakenService _kraken = new();
    private readonly ReplayWatchService _watch;
    private readonly ReplayBuildResolver _resolver;
    private readonly ReplayLibraryStore _libraryStore = new();
    private readonly ReplayLibrary _library;

    private IReadOnlyList<OnlineMatch> _ogAll = [];
    private IReadOnlyList<OnlineMatch> _plusAll = [];

    public ReplaysViewModel()
    {
        _watch = new ReplayWatchService(_settings, _kraken);
        _resolver = new ReplayBuildResolver(_settings);
        _library = _libraryStore.Load();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "Games", "cosmonarchy", "replays"),
            Path.Combine(AppRuntime.Settings.RootPath, "Starcraft", "maps", "replays"),
            Path.Combine(AppRuntime.Settings.RootPath, "Starcraft", "Maps", "replays"),
        };
        _replayRoot = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];

        foreach (var a in ReplayQuery.Races)
        foreach (var b in ReplayQuery.Races)
        {
            var option = new RacePairOption(a, b);
            option.PropertyChanged += (_, _) => ReapplyFilters();
            RacePairs.Add(option);
        }

        foreach (var list in _library.Lists.OrderByDescending(l => l.SavedAt))
            SavedLists.Add(list);
        SelectedSavedList = SavedLists.FirstOrDefault();

        RefreshList();
        RebuildPlayers();
        _ = LoadOnlineAsync(force: false);
    }

    // ---- Filters ----------------------------------------------------------

    public ObservableCollection<RacePairOption> RacePairs { get; } = new();

    [ObservableProperty] private bool _team1v1;
    [ObservableProperty] private bool _team2v2;
    [ObservableProperty] private bool _team3v3;
    [ObservableProperty] private bool _team4v4;
    [ObservableProperty] private bool _teamOther;
    [ObservableProperty] private bool _ignoreObservers = true;
    [ObservableProperty] private bool _favoritesOnly;
    [ObservableProperty] private bool _watchedOnly;
    [ObservableProperty] private bool _withNotesOnly;

    // ---- Saved lists ------------------------------------------------------

    public ObservableCollection<SavedReplayList> SavedLists { get; } = new();

    [ObservableProperty] private SavedReplayList? _selectedSavedList;
    [ObservableProperty] private string _newListName = "";
    [ObservableProperty] private string _listStatus = "";

    // ---- Followed players -------------------------------------------------

    public ObservableCollection<string> Players { get; } = new();

    [ObservableProperty] private string _newPlayer = "";

    // ---- Cosmonarchy OG ---------------------------------------------------

    public ObservableCollection<ReplayRow> OgMatches { get; } = new();

    [ObservableProperty] private string _ogSearch = "";
    [ObservableProperty] private string _ogPlayer = AllPlayers;
    [ObservableProperty] private ReplayRow? _selectedOgRow;
    [ObservableProperty] private string _ogStatus = "Loading the latest games…";
    [ObservableProperty] private string _ogHint = "";
    [ObservableProperty] private bool _ogBusy;

    public bool CanWatchOg => !OgBusy && SelectedOgRow is not null;
    public bool CanUnfollowOg => !OgBusy && OgPlayer != AllPlayers;

    // ---- Cosmonarchy Plus -------------------------------------------------

    public ObservableCollection<ReplayRow> PlusMatches { get; } = new();

    [ObservableProperty] private string _plusSearch = "";
    [ObservableProperty] private string _plusPlayer = AllPlayers;
    [ObservableProperty] private ReplayRow? _selectedPlusRow;
    [ObservableProperty] private string _plusStatus = "Loading the latest games…";
    [ObservableProperty] private string _plusHint = "";
    [ObservableProperty] private bool _plusBusy;

    public bool CanWatchPlus => !PlusBusy && SelectedPlusRow is not null;
    public bool CanUnfollowPlus => !PlusBusy && PlusPlayer != AllPlayers;

    // ---- Selected replay (notes panel) ------------------------------------

    [ObservableProperty] private OnlineMatch? _noteMatch;
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private string _noteTags = "";
    [ObservableProperty] private NoteVisibility _noteVisibility = NoteVisibility.Private;
    [ObservableProperty] private string _noteStatus = "";
    [ObservableProperty] private string _noteHeader = "Select an online game to add notes.";
    [ObservableProperty] private string _noteMeta = "";
    [ObservableProperty] private bool _noteFavorite;

    public IReadOnlyList<NoteVisibility> Visibilities { get; } = [NoteVisibility.Private, NoteVisibility.Shared];

    public bool HasNoteContext => NoteMatch is not null;

    // ---- Local files ------------------------------------------------------

    public ObservableCollection<string> Replays { get; } = new();

    [ObservableProperty] private string? _selectedReplay;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _replayRoot = "";
    [ObservableProperty] private bool _busy;

    public bool CanPlay => !Busy && SelectedReplay is not null;

    partial void OnSelectedReplayChanged(string? value) => OnPropertyChanged(nameof(CanPlay));
    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanPlay));

    partial void OnOgSearchChanged(string value) => ReapplyFilters();
    partial void OnPlusSearchChanged(string value) => ReapplyFilters();
    partial void OnOgPlayerChanged(string value) => ReapplyFilters();
    partial void OnPlusPlayerChanged(string value) => ReapplyFilters();

    partial void OnTeam1v1Changed(bool value) => ReapplyFilters();
    partial void OnTeam2v2Changed(bool value) => ReapplyFilters();
    partial void OnTeam3v3Changed(bool value) => ReapplyFilters();
    partial void OnTeam4v4Changed(bool value) => ReapplyFilters();
    partial void OnTeamOtherChanged(bool value) => ReapplyFilters();
    partial void OnIgnoreObserversChanged(bool value) => ReapplyFilters();
    partial void OnFavoritesOnlyChanged(bool value) => ReapplyFilters();
    partial void OnWatchedOnlyChanged(bool value) => ReapplyFilters();
    partial void OnWithNotesOnlyChanged(bool value) => ReapplyFilters();

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

    partial void OnSelectedOgRowChanged(ReplayRow? value)
    {
        OnPropertyChanged(nameof(CanWatchOg));
        if (value is not null)
            ShowNote(value.Match);
        OgHint = value is null ? "" : Hint(value.Match, ReplayChannel.Original);
    }

    partial void OnSelectedPlusRowChanged(ReplayRow? value)
    {
        OnPropertyChanged(nameof(CanWatchPlus));
        if (value is not null)
            ShowNote(value.Match);
        PlusHint = value is null ? "" : Hint(value.Match, ReplayChannel.Plus);
    }

    partial void OnNoteMatchChanged(OnlineMatch? value) =>
        OnPropertyChanged(nameof(HasNoteContext));

    // ---- Follow players ---------------------------------------------------

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
        ReapplyFilters();
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
        ReapplyFilters();
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

    // ---- Library: notes, favourites, watched ------------------------------

    private void ShowNote(OnlineMatch match)
    {
        NoteMatch = match;
        var note = _library.NoteFor(match.MatchUuid);
        NoteText = note?.Text ?? "";
        NoteTags = note is null ? "" : string.Join(", ", note.Tags);
        NoteVisibility = note?.Visibility ?? NoteVisibility.Private;
        NoteFavorite = _library.IsFavorite(match.MatchUuid);
        NoteHeader = $"{match.PlayersLabel} — {match.MapName} ({match.VersionLabel})";
        var watched = _library.WatchEntry(match.MatchUuid);
        NoteMeta = watched is null
            ? "Not watched yet."
            : $"Watched {watched.WatchedAt.LocalDateTime:yyyy-MM-dd HH:mm} on {watched.Version}.";
        NoteStatus = "";
    }

    [RelayCommand]
    private void SaveNote()
    {
        if (NoteMatch is null)
            return;
        var tags = NoteTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _library.SaveNote(NoteMatch.MatchUuid, NoteText, tags, NoteVisibility);
        _libraryStore.Save(_library);
        NoteStatus = NoteVisibility == NoteVisibility.Shared
            ? "Saved (shared with lists you export)."
            : "Saved privately on this machine.";
        ShowNote(NoteMatch);
        NoteStatus = NoteVisibility == NoteVisibility.Shared
            ? "Saved (shared with lists you export)."
            : "Saved privately on this machine.";
        ReapplyFilters();
    }

    [RelayCommand]
    private void DeleteNote()
    {
        if (NoteMatch is null)
            return;
        _library.DeleteNote(NoteMatch.MatchUuid);
        _libraryStore.Save(_library);
        NoteText = "";
        NoteTags = "";
        NoteVisibility = NoteVisibility.Private;
        NoteStatus = "Note deleted.";
        ReapplyFilters();
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (NoteMatch is null)
            return;
        NoteFavorite = _library.ToggleFavorite(NoteMatch.MatchUuid);
        _libraryStore.Save(_library);
        NoteStatus = NoteFavorite ? "Added to favourites." : "Removed from favourites.";
        ReapplyFilters();
    }

    [RelayCommand]
    private void ShareNote()
    {
        if (NoteMatch is null)
            return;
        var note = _library.NoteFor(NoteMatch.MatchUuid);
        if (note is null)
        {
            NoteStatus = "Save the note first.";
            return;
        }
        var text = ReplaySharing.ExportNote(note, NoteMatch);
        if (text is null)
        {
            NoteStatus = "This note is private. Switch it to Shared and save to share it.";
            return;
        }
        _ = CopyToClipboardAsync(text);
        NoteStatus = "Shared note copied to the clipboard.";
    }

    // ---- Saved lists ------------------------------------------------------

    [RelayCommand]
    private void SaveList()
    {
        var name = NewListName.Trim();
        if (name.Length == 0)
        {
            ListStatus = "Name the list first.";
            return;
        }
        var uuids = OgMatches.Select(r => r.Match.MatchUuid)
            .Concat(PlusMatches.Select(r => r.Match.MatchUuid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var list = new SavedReplayList
        {
            Name = name,
            Filters = BuildFilters(),
            MatchUuids = uuids,
        };
        _library.Lists.Add(list);
        _libraryStore.Save(_library);
        SavedLists.Insert(0, list);
        SelectedSavedList = list;
        NewListName = "";
        ListStatus = $"Saved “{name}” with {uuids.Count} game(s).";
    }

    [RelayCommand]
    private void ApplyList()
    {
        if (SelectedSavedList is not { } list)
            return;
        var filters = list.Filters;
        foreach (var option in RacePairs)
            option.IsSelected = filters.RacePairs.Contains(option.Key);
        Team1v1 = filters.TeamSizes.Contains("1v1");
        Team2v2 = filters.TeamSizes.Contains("2v2");
        Team3v3 = filters.TeamSizes.Contains("3v3");
        Team4v4 = filters.TeamSizes.Contains("4v4");
        TeamOther = filters.TeamSizes.Contains("Other");
        IgnoreObservers = filters.IgnoreObservers;
        FavoritesOnly = filters.FavoritesOnly;
        WatchedOnly = filters.WatchedOnly;
        WithNotesOnly = filters.WithNotesOnly;
        ReapplyFilters();
        ListStatus = $"Applied “{list.Name}” ({list.MatchUuids.Count} saved game(s)).";
    }

    [RelayCommand]
    private void DeleteList()
    {
        if (SelectedSavedList is not { } list)
            return;
        _library.Lists.Remove(list);
        _libraryStore.Save(_library);
        SavedLists.Remove(list);
        SelectedSavedList = SavedLists.FirstOrDefault();
        ListStatus = $"Deleted “{list.Name}”.";
    }

    [RelayCommand]
    private void ShareList()
    {
        if (SelectedSavedList is not { } list)
            return;
        var json = ReplaySharing.ExportList(list, _ogAll.Concat(_plusAll), _library);
        var path = ReplaySharing.SaveToDownloads(list.Name, json);
        _ = CopyToClipboardAsync(json);
        ListStatus = $"Shared “{list.Name}” → {path} (also copied to the clipboard).";
    }

    [RelayCommand]
    private void ClearFilters()
    {
        foreach (var option in RacePairs)
            option.IsSelected = false;
        Team1v1 = Team2v2 = Team3v3 = Team4v4 = TeamOther = false;
        FavoritesOnly = WatchedOnly = WithNotesOnly = false;
        IgnoreObservers = true;
        OgSearch = PlusSearch = "";
        ReapplyFilters();
        ListStatus = "Filters cleared.";
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        if (AppRuntime.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private ReplayFilters BuildFilters() => new()
    {
        RacePairs = RacePairs.Where(p => p.IsSelected)
            .Select(p => p.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase),
        TeamSizes = new[]
            {
                (Team1v1, "1v1"), (Team2v2, "2v2"), (Team3v3, "3v3"),
                (Team4v4, "4v4"), (TeamOther, "Other"),
            }
            .Where(t => t.Item1)
            .Select(t => t.Item2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase),
        IgnoreObservers = IgnoreObservers,
        FavoritesOnly = FavoritesOnly,
        WatchedOnly = WatchedOnly,
        WithNotesOnly = WithNotesOnly,
    };

    // ---- Online data ------------------------------------------------------

    [RelayCommand]
    private Task RefreshOnlineAsync() => LoadOnlineAsync(force: true);

    [RelayCommand]
    private async Task WatchOgAsync() => await WatchAsync(SelectedOgRow?.Match, isPlus: false);

    [RelayCommand]
    private async Task WatchPlusAsync() => await WatchAsync(SelectedPlusRow?.Match, isPlus: true);

    [RelayCommand]
    private async Task DownloadOgAsync() => await DownloadOnlyAsync(SelectedOgRow?.Match, isPlus: false);

    [RelayCommand]
    private async Task DownloadPlusAsync() => await DownloadOnlyAsync(SelectedPlusRow?.Match, isPlus: true);

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
            _library.MarkWatched(match.MatchUuid, match.FileId, match.VersionLabel);
            _libraryStore.Save(_library);
            SetStatus(isPlus, $"Launched {match.FileName} on {build.Label} {build.FullSha[..8]}.");
            ReapplyFilters();
            ShowNote(match);
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

            var plusRelease = ForkShas(prerelease: false);
            var plusPrerelease = ForkShas(prerelease: true);
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
            ReapplyFilters();
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

    private void ReapplyFilters()
    {
        var filters = BuildFilters();
        var og = ReplayQuery.Apply(_ogAll, filters, _library);
        var plus = ReplayQuery.Apply(_plusAll, filters, _library);
        Populate(OgMatches, og, OgPlayer, OgSearch, ReplayChannel.Original);
        Populate(PlusMatches, plus, PlusPlayer, PlusSearch, ReplayChannel.Plus);
    }

    private void Populate(
        ObservableCollection<ReplayRow> target, IEnumerable<OnlineMatch> source,
        string player, string search, ReplayChannel channel)
    {
        var matches = source;
        if (player != AllPlayers)
            matches = matches.Where(m => MatchHasPlayer(m, player));
        if (!string.IsNullOrWhiteSpace(search))
            matches = matches.Where(m => MatchContains(m, search.Trim()));

        var filtered = !BuildFilters().IsDefault || player != AllPlayers ||
                       !string.IsNullOrWhiteSpace(search);
        var limit = filtered ? FilteredCount : LatestCount;
        var list = matches.OrderByDescending(m => m.UploadedAt).Take(limit).ToList();

        var previous = channel == ReplayChannel.Plus
            ? SelectedPlusRow?.Match.MatchUuid
            : SelectedOgRow?.Match.MatchUuid;

        target.Clear();
        foreach (var match in list)
        {
            var note = _library.NoteFor(match.MatchUuid);
            target.Add(new ReplayRow(
                match,
                _library.IsFavorite(match.MatchUuid),
                _library.WasWatched(match.MatchUuid),
                note is { Text.Length: > 0 } || (note?.Tags.Count ?? 0) > 0));
        }

        var restored = previous is null
            ? null
            : target.FirstOrDefault(r => r.Match.MatchUuid.Equals(previous, StringComparison.OrdinalIgnoreCase));
        if (channel == ReplayChannel.Plus)
            SelectedPlusRow = restored ?? target.FirstOrDefault();
        else
            SelectedOgRow = restored ?? target.FirstOrDefault();

        var name = channel == ReplayChannel.Plus ? "Cosmonarchy Plus" : "Cosmonarchy OG";
        var total = channel == ReplayChannel.Plus ? _plusAll.Count : _ogAll.Count;
        var status = $"{list.Count} shown · {total} {name} games";
        if (player != AllPlayers)
            status += $" · {player}";
        if (!string.IsNullOrWhiteSpace(search))
            status += $" · “{search.Trim()}”";
        SetStatus(channel == ReplayChannel.Plus, status + ".");
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
