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
/// watched, note) for the list badges, and the statistics-account
/// certification ("✓" when the signed-in account certified it as theirs).</summary>
public sealed record ReplayRow(
    OnlineMatch Match, bool Favorite, bool Watched, bool HasNote, bool Desynced, bool Certified)
{
    public string PlayersLabel => Match.PlayersLabel;
    public string DateLabel => Match.DateLabel;
    public string MapName => Match.MapName;
    public string DurationLabel => Match.DurationLabel;
    public string VersionLabel => Match.VersionLabel;

    public string Badges => string.Concat(
        Favorite ? "★ " : "",
        Watched ? "▶ " : "",
        HasNote ? "✎ " : "",
        Certified ? "✓ " : "",
        Desynced ? "⚠ " : "");
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

/// <summary>One followed player for the Following tab, with how many of
/// their games are in each channel.</summary>
public sealed record FollowedRow(string Name, int OgCount, int PlusCount)
{
    public string OgLabel => OgCount.ToString();
    public string PlusLabel => PlusCount.ToString();

    public string ChannelLabel =>
        OgCount > 0 && PlusCount > 0 ? "OG + Plus"
        : OgCount > 0 ? "OG"
        : PlusCount > 0 ? "Plus"
        : "no games";
}

/// <summary>One replay of a followed player, with the channel it belongs to.</summary>
public sealed record FollowedReplayRow(
    OnlineMatch Match, ReplayChannel Channel, bool Favorite, bool Watched, bool HasNote, bool Desynced, bool Certified)
{
    public string PlayersLabel => Match.PlayersLabel;
    public string DateLabel => Match.DateLabel;
    public string MapName => Match.MapName;
    public string DurationLabel => Match.DurationLabel;
    public string VersionLabel => Match.VersionLabel;

    public string ChannelLabel => Channel == ReplayChannel.Plus ? "Plus" : "OG";

    public string Badges => string.Concat(
        Favorite ? "★ " : "",
        Watched ? "▶ " : "",
        HasNote ? "✎ " : "",
        Certified ? "✓ " : "",
        Desynced ? "⚠ " : "");
}

public partial class ReplaysViewModel : ViewModelBase
{
    public const string AllPlayers = "All players";

    private const int LatestCount = 20;
    private const int FilteredCount = 100;
    private const int ShowMoreStep = 200;

    private readonly AppSettings _settings = AppRuntime.Settings;
    private readonly ReplayKrakenService _kraken = new();
    private readonly ReplayWatchService _watch;
    private readonly ReplayBuildResolver _resolver;
    private readonly ReplayLibrary _library = AppRuntime.Library;

    private IReadOnlyList<OnlineMatch> _ogAll = [];
    private IReadOnlyList<OnlineMatch> _plusAll = [];

    public ReplaysViewModel()
    {
        _watch = new ReplayWatchService(_settings, _kraken);
        _resolver = new ReplayBuildResolver(_settings);
        AppRuntime.LibraryChanged += OnLibraryChanged;
        AppRuntime.CertificationsChanged += OnCertificationsChanged;
        AppRuntime.LocalAccountChanged += OnCertificationsChanged;

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

    /// <summary>Certification filter for the online lists. Ties into the
    /// statistics account: Certified = games the signed-in account checked as
    /// really theirs, Uncertified = the rest (the impostor check).</summary>
    public static IReadOnlyList<CertificationFilter> CertificationFilters { get; } =
        [CertificationFilter.All, CertificationFilter.Certified, CertificationFilter.Uncertified];

    [ObservableProperty] private CertificationFilter _certFilter = CertificationFilter.All;

    // ---- Saved lists ------------------------------------------------------

    public ObservableCollection<SavedReplayList> SavedLists { get; } = new();

    [ObservableProperty] private SavedReplayList? _selectedSavedList;
    [ObservableProperty] private SavedReplayList? _selectedAddList;
    [ObservableProperty] private string _newListName = "";
    [ObservableProperty] private string _newListTitle = "";
    [ObservableProperty] private string _listStatus = "";

    // ---- Followed players -------------------------------------------------

    public ObservableCollection<string> Players { get; } = new();

    [ObservableProperty] private string _newPlayer = "";

    // ---- Following tab ----------------------------------------------------

    public ObservableCollection<FollowedRow> FollowedPlayers { get; } = new();
    public ObservableCollection<FollowedReplayRow> FollowedMatches { get; } = new();

    [ObservableProperty] private FollowedRow? _selectedFollowed;
    [ObservableProperty] private FollowedReplayRow? _selectedFollowedMatch;
    [ObservableProperty] private string _followedStatus =
        "Follow players here or on the OG/Plus tabs; their games from both channels appear below.";
    [ObservableProperty] private bool _followedBusy;

    public bool CanWatchFollowed => !FollowedBusy && SelectedFollowedMatch is not null;
    public bool CanUnfollowSelected => SelectedFollowed is not null;

    partial void OnSelectedFollowedChanged(FollowedRow? value)
    {
        OnPropertyChanged(nameof(CanUnfollowSelected));
        RebuildFollowedMatches();
    }

    partial void OnSelectedFollowedMatchChanged(FollowedReplayRow? value)
    {
        OnPropertyChanged(nameof(CanWatchFollowed));
        if (value is not null)
            ShowNote(value.Match);
    }

    partial void OnFollowedBusyChanged(bool value) =>
        OnPropertyChanged(nameof(CanWatchFollowed));

    // ---- Cosmonarchy OG ---------------------------------------------------

    public ObservableCollection<ReplayRow> OgMatches { get; } = new();

    [ObservableProperty] private string _ogSearch = "";
    [ObservableProperty] private string _ogPlayer = AllPlayers;
    [ObservableProperty] private ReplayRow? _selectedOgRow;
    [ObservableProperty] private string _ogStatus = "Loading the latest games…";
    [ObservableProperty] private string _ogHint = "";
    [ObservableProperty] private bool _ogBusy;
    [ObservableProperty] private int _ogVisible = LatestCount;
    [ObservableProperty] private bool _canShowMoreOg;

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
    [ObservableProperty] private int _plusVisible = LatestCount;
    [ObservableProperty] private bool _canShowMorePlus;

    public bool CanWatchPlus => !PlusBusy && SelectedPlusRow is not null;
    public bool CanUnfollowPlus => !PlusBusy && PlusPlayer != AllPlayers;

    // ---- Download progress (shared by both online tabs) --------------------

    /// <summary>Download progress as a fraction (0-1), or -1 while nothing
    /// is downloading. Status messages carry "NN%"; this feeds the bars.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadPercent))]
    [NotifyPropertyChangedFor(nameof(OgBarIndeterminate))]
    [NotifyPropertyChangedFor(nameof(PlusBarIndeterminate))]
    [NotifyPropertyChangedFor(nameof(LocalBarIndeterminate))]
    private double _downloadFraction = -1;

    public double DownloadPercent => DownloadFraction < 0 ? 0 : DownloadFraction * 100;

    /// <summary>An indeterminate bar while busy work (git fetches, unpacking)
    /// runs without a byte percentage.</summary>
    public bool OgBarIndeterminate => OgBusy && DownloadFraction < 0;
    public bool PlusBarIndeterminate => PlusBusy && DownloadFraction < 0;

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

    /// <summary>An indeterminate bar while busy work (git fetches, unpacking)
    /// runs without a byte percentage.</summary>
    public bool LocalBarIndeterminate => Busy && DownloadFraction < 0;

    partial void OnSelectedReplayChanged(string? value) => OnPropertyChanged(nameof(CanPlay));
    partial void OnBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(LocalBarIndeterminate));
    }

    partial void OnOgSearchChanged(string value) => ReapplyFilters();
    partial void OnPlusSearchChanged(string value) => ReapplyFilters();
    partial void OnOgPlayerChanged(string value) => ReapplyFilters();
    partial void OnPlusPlayerChanged(string value) => ReapplyFilters();
    partial void OnCertFilterChanged(CertificationFilter value) => ReapplyFilters();

    partial void OnTeam1v1Changed(bool value) => ReapplyFilters();
    partial void OnTeam2v2Changed(bool value) => ReapplyFilters();
    partial void OnTeam3v3Changed(bool value) => ReapplyFilters();
    partial void OnTeam4v4Changed(bool value) => ReapplyFilters();
    partial void OnTeamOtherChanged(bool value) => ReapplyFilters();
    partial void OnIgnoreObserversChanged(bool value) => ReapplyFilters();
    partial void OnFavoritesOnlyChanged(bool value) => ReapplyFilters();
    partial void OnWatchedOnlyChanged(bool value) => ReapplyFilters();
    partial void OnWithNotesOnlyChanged(bool value) => ReapplyFilters();
    partial void OnOgVisibleChanged(int value) => ReapplyFilters();
    partial void OnPlusVisibleChanged(int value) => ReapplyFilters();

    partial void OnOgBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanWatchOg));
        OnPropertyChanged(nameof(CanUnfollowOg));
        OnPropertyChanged(nameof(OgBarIndeterminate));
    }

    partial void OnPlusBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanWatchPlus));
        OnPropertyChanged(nameof(CanUnfollowPlus));
        OnPropertyChanged(nameof(PlusBarIndeterminate));
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

    partial void OnNoteMatchChanged(OnlineMatch? value)
    {
        OnPropertyChanged(nameof(HasNoteContext));
        OnPropertyChanged(nameof(CanCertify));
        OnPropertyChanged(nameof(CertifyContent));
    }

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
        RebuildFollowed();
        ReapplyFilters();
    }

    [RelayCommand]
    private void UnfollowOg() => Unfollow(OgPlayer);

    [RelayCommand]
    private void UnfollowPlus() => Unfollow(PlusPlayer);

    [RelayCommand]
    private void UnfollowSelected() => Unfollow(SelectedFollowed?.Name ?? "");

    private void Unfollow(string player)
    {
        if (player == AllPlayers || player.Length == 0)
            return;
        _settings.FollowedPlayers.RemoveAll(p => p.Equals(player, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        RebuildPlayers();
        RebuildFollowed();
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

    // ---- Following tab ----------------------------------------------------

    /// <summary>Rebuilds the followed-player list with per-channel game
    /// counts, so OG and Plus followers are told apart.</summary>
    private void RebuildFollowed()
    {
        static void Count(Dictionary<string, (int Og, int Plus)> counts, IEnumerable<OnlineMatch> matches, bool plus)
        {
            foreach (var match in matches)
            {
                foreach (var name in FollowedNames(match))
                {
                    counts.TryGetValue(name, out var existing);
                    if (plus)
                        existing.Plus++;
                    else
                        existing.Og++;
                    counts[name] = existing;
                }
            }
        }

        var counts = new Dictionary<string, (int Og, int Plus)>(StringComparer.OrdinalIgnoreCase);
        Count(counts, _ogAll, plus: false);
        Count(counts, _plusAll, plus: true);

        var previous = SelectedFollowed?.Name;
        FollowedPlayers.Clear();
        foreach (var name in _settings.FollowedPlayers.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            counts.TryGetValue(name, out var c);
            FollowedPlayers.Add(new FollowedRow(name, c.Og, c.Plus));
        }
        SelectedFollowed = FollowedPlayers.FirstOrDefault(f =>
                f.Name.Equals(previous, StringComparison.OrdinalIgnoreCase))
            ?? FollowedPlayers.FirstOrDefault();
        RebuildFollowedMatches();
    }

    private static IEnumerable<string> FollowedNames(OnlineMatch match)
    {
        yield return match.UploaderName;
        foreach (var player in match.Players)
            yield return player.Name;
    }

    private void RebuildFollowedMatches()
    {
        var previous = SelectedFollowedMatch?.Match.MatchUuid;
        FollowedMatches.Clear();
        if (SelectedFollowed is not { } followed)
        {
            FollowedStatus = FollowedPlayers.Count == 0
                ? "Follow players here or on the OG/Plus tabs; their games from both channels appear below."
                : "Select a player above to see their games.";
            return;
        }

        var rows = _ogAll.Where(m => MatchHasPlayer(m, followed.Name))
            .Select(m => new FollowedReplayRow(m, ReplayChannel.Original, false, false, false, m.HasDesync, false))
            .Concat(_plusAll.Where(m => MatchHasPlayer(m, followed.Name))
                .Select(m => new FollowedReplayRow(m, ReplayChannel.Plus, false, false, false, m.HasDesync, false)))
            .OrderByDescending(r => r.Match.UploadedAt)
            .ToList();

        foreach (var row in rows)
        {
            var match = row.Match;
            var note = _library.NoteFor(match.MatchUuid);
            FollowedMatches.Add(row with
            {
                Favorite = _library.IsFavorite(match.MatchUuid),
                Watched = _library.WasWatched(match.MatchUuid),
                HasNote = note is { Text.Length: > 0 } || (note?.Tags.Count ?? 0) > 0,
                Certified = IsCertified(match.MatchUuid),
            });
        }
        SelectedFollowedMatch = FollowedMatches.FirstOrDefault(r =>
                r.Match.MatchUuid.Equals(previous, StringComparison.OrdinalIgnoreCase))
            ?? FollowedMatches.FirstOrDefault();
        FollowedStatus = $"{FollowedMatches.Count} game(s) by {followed.Name} across both channels.";
    }

    [RelayCommand]
    private async Task WatchFollowedAsync() => await WatchFollowedRowAsync(SelectedFollowedMatch);

    [RelayCommand]
    private async Task DownloadFollowedAsync() => await DownloadFollowedRowAsync(SelectedFollowedMatch);

    private async Task WatchFollowedRowAsync(FollowedReplayRow? row)
    {
        if (row is null)
            return;
        FollowedBusy = true;
        try
        {
            var progress = new Progress<string>(s => FollowedStatus = s);
            FollowedStatus = "Working out the build…";
            var build = await Task.Run(() => _watch.WatchAsync(row.Match, progress, CancellationToken.None));
            _library.MarkWatched(row.Match.MatchUuid, row.Match.FileId, row.Match.VersionLabel);
            AppRuntime.SaveLibrary();
            FollowedStatus = $"Launched {row.Match.FileName} on {build.Label} {build.FullSha[..8]}.";
            RebuildFollowed();
            ShowNote(row.Match);
        }
        catch (Exception ex)
        {
            FollowedStatus = $"Could not watch: {ex.Message}";
        }
        finally
        {
            FollowedBusy = false;
        }
    }

    private async Task DownloadFollowedRowAsync(FollowedReplayRow? row)
    {
        if (row is null)
            return;
        FollowedBusy = true;
        try
        {
            var progress = new Progress<string>(s => FollowedStatus = s);
            var path = await _kraken.DownloadReplayAsync(row.Match, ReplayRoot, progress);
            FollowedStatus = $"Saved {Path.GetFileName(path)} to {ReplayRoot}.";
            RefreshList();
        }
        catch (Exception ex)
        {
            FollowedStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            FollowedBusy = false;
        }
    }

    // ---- Library: notes, favourites, watched ------------------------------

    /// <summary>The library changed (this tab or the Favourites tab saved
    /// something): rebuild the list combos and refresh everything that
    /// renders library state.</summary>
    private void OnLibraryChanged()
    {
        var listId = SelectedSavedList?.Id;
        var addId = SelectedAddList?.Id;
        SavedLists.Clear();
        foreach (var list in _library.Lists.OrderByDescending(l => l.SavedAt))
            SavedLists.Add(list);
        SelectedSavedList = SavedLists.FirstOrDefault(l => l.Id == listId) ?? SavedLists.FirstOrDefault();
        SelectedAddList = SavedLists.FirstOrDefault(l => l.Id == addId);
        ReapplyFilters();
        RebuildFollowed();
        if (NoteMatch is not null)
            ShowNote(NoteMatch);
    }

    /// <summary>The statistics account changed or certified/un-certified a
    /// match: refresh the ✓ badges, the certification filter and the note
    /// panel's certify button.</summary>
    private void OnCertificationsChanged()
    {
        OnPropertyChanged(nameof(CanCertify));
        OnPropertyChanged(nameof(CertifyContent));
        ReapplyFilters();
        if (NoteMatch is not null)
            ShowNote(NoteMatch);
    }

    /// <summary>True when the signed-in statistics account's claimed name
    /// appears in the selected match and the account exists — i.e. the match
    /// can be certified as theirs.</summary>
    public bool CanCertify =>
        AppRuntime.CurrentLocalAccount is { } account &&
        NoteMatch is { } match &&
        (match.UploaderName.Equals(account.PlayerName, StringComparison.OrdinalIgnoreCase) ||
         match.Players.Any(p => p.Name.Equals(account.PlayerName, StringComparison.OrdinalIgnoreCase)));

    public string CertifyContent => NoteMatch is not null && IsCertified(NoteMatch.MatchUuid)
        ? "Uncertify"
        : "Certify as me";

    /// <summary>Certify (or un-certify) the selected match as genuinely played
    /// by the signed-in statistics account.</summary>
    [RelayCommand]
    private async Task ToggleCertifyAsync()
    {
        var account = AppRuntime.CurrentLocalAccount;
        if (account is null || NoteMatch is null)
            return;
        var certified = !IsCertified(NoteMatch.MatchUuid);
        await AppRuntime.AccountBackend.SetCertificationAsync(
            new AccountSession(account, ""), NoteMatch.MatchUuid, certified);
        AppRuntime.NotifyCertificationsChanged();
        NoteStatus = certified
            ? $"Certified as yours. Your statistics count it and the ✓ badge is set."
            : "Uncertified. It no longer feeds your statistics and stays searchable as uncertified.";
    }

    /// <summary>Is this match certified by the signed-in statistics account?</summary>
    private static bool IsCertified(string matchUuid) =>
        AppRuntime.CurrentLocalAccount is { } account &&
        LocalAccountBackend.CertifiedSet(account).Contains(matchUuid);

    private void ShowNote(OnlineMatch match)
    {
        NoteMatch = match;
        var note = _library.NoteFor(match.MatchUuid);
        NoteText = note?.Text ?? "";
        NoteTags = note is null ? "" : string.Join(", ", note.Tags);
        NoteVisibility = note?.Visibility ?? NoteVisibility.Private;
        NoteFavorite = _library.IsFavorite(match.MatchUuid);
        NoteHeader = $"{match.PlayersLabel} — {match.MapName} ({match.VersionLabel})";
        var favourite = _library.FavoriteFor(match.MatchUuid);
        var watched = _library.WatchEntry(match.MatchUuid);
        var bits = new List<string>
        {
            favourite is null
                ? "Not favourited."
                : favourite.AddedAt == DateTimeOffset.MinValue
                    ? "★ Favourited (added before the launcher recorded dates)."
                    : $"★ Favourited {favourite.AddedAt.LocalDateTime:yyyy-MM-dd HH:mm}",
            watched is null
                ? "Not watched yet."
                : $"Watched {watched.WatchedAt.LocalDateTime:yyyy-MM-dd HH:mm} on {watched.Version}.",
        };
        if (match.HasDesync)
            bits.Add("⚠ This game desynced.");
        var cert = AppRuntime.CurrentLocalAccount is { } certAccount
            ? LocalAccountBackend.CertificationFor(certAccount, match.MatchUuid)
            : null;
        bits.Add(cert is not null
            ? $"✓ Certified by {cert.IdentityLabel} on {cert.CertifiedAt.LocalDateTime:yyyy-MM-dd HH:mm}."
            : AppRuntime.CurrentLocalAccount is null
                ? "Create a Statistics account to certify games as really yours."
                : "Not certified as yours yet.");
        NoteMeta = string.Join(" · ", bits);
        NoteStatus = "";
    }

    [RelayCommand]
    private void SaveNote()
    {
        if (NoteMatch is null)
            return;
        var tags = NoteTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _library.SaveNote(NoteMatch.MatchUuid, NoteText, tags, NoteVisibility);
        AppRuntime.SaveLibrary();
        ShowNote(NoteMatch);
        NoteStatus = NoteVisibility == NoteVisibility.Shared
            ? "Saved (included when you share this note or lists)."
            : "Saved privately on this machine.";
        ReapplyFilters();
    }

    [RelayCommand]
    private void DeleteNote()
    {
        if (NoteMatch is null)
            return;
        _library.DeleteNote(NoteMatch.MatchUuid);
        AppRuntime.SaveLibrary();
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
        NoteFavorite = _library.ToggleFavorite(NoteMatch.MatchUuid, ReplaySnapshot.From(NoteMatch));
        AppRuntime.SaveLibrary();
        NoteStatus = NoteFavorite ? "Added to favourites." : "Removed from favourites.";
        ReapplyFilters();
    }

    [RelayCommand]
    private void AddNoteMatchToList()
    {
        if (NoteMatch is null || SelectedAddList is not { } list)
            return;
        var uuid = NoteMatch.MatchUuid;
        if (!list.MatchUuids.Any(u => u.Equals(uuid, StringComparison.OrdinalIgnoreCase)))
        {
            list.MatchUuids.Insert(0, uuid);
            AppRuntime.SaveLibrary();
        }
        NoteStatus = $"Added “{NoteMatch.PlayersLabel}” to “{list.Name}” ({list.MatchUuids.Count} game(s)).";
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
        if (note.Visibility != NoteVisibility.Shared)
        {
            NoteStatus = "This note is private. Switch it to Shared and save to share it.";
            return;
        }
        var payload = ReplaySharing.ExportNotePayload(note, NoteMatch, _library.FavoriteFor(NoteMatch.MatchUuid));
        var message = ReplaySharing.DiscordNoteMessage(payload, note, NoteMatch);
        _ = CopyToClipboardAsync(message);
        DesktopActions.OpenDiscord();
        NoteStatus = "Copied a Discord-ready message and opened Discord — paste it into any channel. The recipient imports it in Favourites → Import.";
    }

    [RelayCommand]
    private async Task PostNoteToDiscordAsync()
    {
        if (NoteMatch is null)
            return;
        var note = _library.NoteFor(NoteMatch.MatchUuid);
        if (note is null || note.Visibility != NoteVisibility.Shared)
        {
            NoteStatus = "Save the note as Shared first.";
            return;
        }
        var payload = ReplaySharing.ExportNotePayload(note, NoteMatch, _library.FavoriteFor(NoteMatch.MatchUuid));
        NoteStatus = await PostToDiscordAsync(
            ReplaySharing.DiscordNoteMessage(payload, note, NoteMatch), fileName: null, fileContent: null);
    }

    private static async Task<string> PostToDiscordAsync(string content, string? fileName, string? fileContent)
    {
        var webhook = AppRuntime.Settings.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(webhook) || !DiscordWebhook.IsValidWebhookUrl(webhook))
            return "No Discord webhook configured. In Discord: channel → Integrations → Webhooks → New, then paste the URL in Settings.";
        try
        {
            var poster = new DiscordWebhook();
            if (fileContent is null)
                await poster.PostAsync(webhook, content);
            else
                await poster.PostWithFileAsync(webhook, content, fileName ?? "Cosmonarchy-share.json", fileContent);
            return "Posted to Discord.";
        }
        catch (Exception ex)
        {
            return $"Could not post to Discord: {ex.Message}";
        }
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
            Title = NewListTitle.Trim(),
            Filters = BuildFilters(),
            MatchUuids = uuids,
        };
        _library.Lists.Add(list);
        AppRuntime.SaveLibrary(); // raises LibraryChanged → SavedLists rebuilt, newest selected
        NewListName = "";
        NewListTitle = "";
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
        AppRuntime.SaveLibrary(); // raises LibraryChanged → SavedLists rebuilt
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
        ListStatus = $"Shared “{list.Name}” → {path}. The share file is in Downloads and the payload is on the clipboard — attach the file in Discord (or paste the payload anywhere); the recipient imports it in Favourites → Import.";
    }

    [RelayCommand]
    private async Task PostListToDiscordAsync()
    {
        if (SelectedSavedList is not { } list)
            return;
        var json = ReplaySharing.ExportList(list, _ogAll.Concat(_plusAll), _library);
        var noteCount = list.MatchUuids.Count(u =>
            _library.NoteFor(u) is { Visibility: NoteVisibility.Shared });
        var content = ReplaySharing.DiscordListMessage(list.Name, list.Title, list.MatchUuids.Count, noteCount);
        ListStatus = await PostToDiscordAsync(content, $"Cosmonarchy-{SafeFileName(list.Name)}.json", json);
    }

    private static string SafeFileName(string name)
    {
        var safe = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        safe = string.Concat(safe.Select(c => c == ' ' ? '-' : c));
        return safe.Length == 0 ? "replay-list" : safe;
    }

    [RelayCommand]
    private void ClearFilters()
    {
        foreach (var option in RacePairs)
            option.IsSelected = false;
        Team1v1 = Team2v2 = Team3v3 = Team4v4 = TeamOther = false;
        FavoritesOnly = WatchedOnly = WithNotesOnly = false;
        IgnoreObservers = true;
        CertFilter = CertificationFilter.All;
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
    private void ShowMoreOg() => OgVisible += ShowMoreStep;

    [RelayCommand]
    private void ShowMorePlus() => PlusVisible += ShowMoreStep;

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
        DownloadFraction = -1;
        try
        {
            var progress = new Progress<string>(s => SetStatusWithProgress(isPlus, s));
            SetStatus(isPlus, "Working out the build…");
            var build = await Task.Run(() => _watch.WatchAsync(match, progress, CancellationToken.None));
            _library.MarkWatched(match.MatchUuid, match.FileId, match.VersionLabel);
            AppRuntime.SaveLibrary();
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
            DownloadFraction = -1;
        }
    }

    private async Task DownloadOnlyAsync(OnlineMatch? match, bool isPlus)
    {
        if (match is null)
            return;
        SetBusy(isPlus, true);
        DownloadFraction = -1;
        try
        {
            var progress = new Progress<string>(s => SetStatusWithProgress(isPlus, s));
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
            DownloadFraction = -1;
        }
    }

    private async Task LoadOnlineAsync(bool force)
    {
        OgBusy = true;
        PlusBusy = true;
        DownloadFraction = -1;
        try
        {
            var progress = new Progress<string>(s =>
            {
                OgStatus = s;
                DownloadFraction = HttpDownload.PercentOf(s) is { } percent ? percent / 100 : -1;
            });
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
            RebuildFollowed();
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
            DownloadFraction = -1;
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
        if (CertFilter != CertificationFilter.All)
            matches = matches.Where(m => (CertFilter == CertificationFilter.Certified) == IsCertified(m.MatchUuid));

        var filtered = !BuildFilters().IsDefault || player != AllPlayers ||
                       !string.IsNullOrWhiteSpace(search);
        var baseline = filtered ? FilteredCount : LatestCount;
        var visible = channel == ReplayChannel.Plus ? PlusVisible : OgVisible;
        var all = matches.OrderByDescending(m => m.UploadedAt).ToList();
        var list = all.Take(Math.Max(baseline, visible)).ToList();
        var canShowMore = list.Count < all.Count;
        if (channel == ReplayChannel.Plus)
            CanShowMorePlus = canShowMore;
        else
            CanShowMoreOg = canShowMore;

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
                note is { Text.Length: > 0 } || (note?.Tags.Count ?? 0) > 0,
                match.HasDesync,
                IsCertified(match.MatchUuid)));
        }

        var restored = previous is null
            ? null
            : target.FirstOrDefault(r => r.Match.MatchUuid.Equals(previous, StringComparison.OrdinalIgnoreCase));
        if (channel == ReplayChannel.Plus)
            SelectedPlusRow = restored ?? target.FirstOrDefault();
        else
            SelectedOgRow = restored ?? target.FirstOrDefault();

        var name = channel == ReplayChannel.Plus ? "Cosmonarchy Plus" : "Cosmonarchy OG";
        var status = $"{list.Count} shown · {all.Count} {name} games";
        if (player != AllPlayers)
            status += $" · {player}";
        if (!string.IsNullOrWhiteSpace(search))
            status += $" · “{search.Trim()}”";
        if (canShowMore)
            status += " · press “Show more” for the rest";
        SetStatus(channel == ReplayChannel.Plus, status + ".");
    }

    private static bool MatchHasPlayer(OnlineMatch match, string player) =>
        match.UploaderName.Equals(player, StringComparison.OrdinalIgnoreCase) ||
        match.Players.Any(p => p.Name.Equals(player, StringComparison.OrdinalIgnoreCase));

    private static bool MatchContains(OnlineMatch match, string text)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (text.Equals("certified", comparison))
            return IsCertified(match.MatchUuid);
        if (text.Equals("uncertified", comparison))
            return !IsCertified(match.MatchUuid);
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

    /// <summary>Set the tab status and, when the message carries a download
    /// percentage, move the shared progress bar.</summary>
    private void SetStatusWithProgress(bool isPlus, string text)
    {
        SetStatus(isPlus, text);
        DownloadFraction = HttpDownload.PercentOf(text) is { } percent ? percent / 100 : -1;
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
        DownloadFraction = -1;
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
            DownloadFraction = -1;
        }
    }

    private async Task PlayReplayFileAsync(string replay)
    {
        var version = AppRuntime.Versions.Selected
                      ?? throw new InvalidOperationException("No installed version — install one on the Play page first.");
        var repo = RepoCatalog.Find(version.RepoId)
                   ?? throw new InvalidOperationException("No repository configured.");
        var service = AppRuntime.CreateInstaller(repo, version);
        var progress = new Progress<string>(s =>
        {
            Status = s;
            DownloadFraction = HttpDownload.PercentOf(s) is { } percent ? percent / 100 : -1;
        });
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
