using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>How the favourites list splits new (unwatched) from watched games.</summary>
public enum FavoritesFilter
{
    All,
    New,
    Watched,
}

/// <summary>One favourited replay for the Favourites tab: the date it was
/// added, its match info (snapshot or online), watched state and note.</summary>
public sealed record FavoriteRow(
    string MatchUuid,
    DateTimeOffset AddedAt,
    DateTimeOffset UploadedAt,
    string MapName,
    string PlayersLabel,
    string VersionLabel,
    string FileId,
    bool HasDesync,
    WatchedReplay? Watched,
    ReplayNote? Note)
{
    public bool IsWatched => Watched is not null;

    public string StatusLabel => IsWatched ? "Watched" : "New";

    public string AddedLabel => AddedAt == DateTimeOffset.MinValue
        ? "earlier"
        : AddedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string DateLabel => UploadedAt == DateTimeOffset.MinValue
        ? ""
        : UploadedAt.LocalDateTime.ToString("yyyy-MM-dd");

    public bool HasNote => (Note?.Text.Length ?? 0) > 0 || (Note?.Tags.Count ?? 0) > 0;

    public string Badges => string.Concat(
        HasDesync ? "⚠ " : "",
        HasNote ? "✎ " : "");

    public string WatchTooltip => string.Concat(
        IsWatched
            ? $"Watched {Watched!.WatchedAt.LocalDateTime:yyyy-MM-dd HH:mm} on {Watched.Version}."
            : "Not watched yet.",
        HasDesync ? " Desynced game." : "");
}

public partial class FavouritesViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppRuntime.Settings;
    private readonly ReplayLibrary _library = AppRuntime.Library;
    private readonly ReplayKrakenService _kraken = new();

    private Dictionary<string, OnlineMatch> _online = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressListFieldSync;

    public FavouritesViewModel()
    {
        AppRuntime.LibraryChanged += OnLibraryChanged;
        RebuildLists();
        RebuildFavorites();
        _ = LoadOnlineAsync(force: false);
    }

    // ---- Favourites list --------------------------------------------------

    public ObservableCollection<FavoriteRow> Favorites { get; } = new();

    public IReadOnlyList<FavoritesFilter> Filters { get; } =
        [FavoritesFilter.All, FavoritesFilter.New, FavoritesFilter.Watched];

    [ObservableProperty] private FavoritesFilter _filter = FavoritesFilter.All;
    [ObservableProperty] private FavoriteRow? _selectedFavorite;
    [ObservableProperty] private string _favoritesStatus = "";
    [ObservableProperty] private bool _favoritesBusy;

    partial void OnFilterChanged(FavoritesFilter value) => RebuildFavorites();

    [RelayCommand]
    private Task RefreshOnlineAsync() => LoadOnlineAsync(force: true);

    // ---- Saved lists ------------------------------------------------------

    public ObservableCollection<SavedReplayList> SavedLists { get; } = new();

    [ObservableProperty] private SavedReplayList? _selectedSavedList;
    [ObservableProperty] private SavedReplayList? _selectedAddList;
    [ObservableProperty] private string _listName = "";
    [ObservableProperty] private string _listTitle = "";
    [ObservableProperty] private string _listStatus = "";

    // ---- Note editor ------------------------------------------------------

    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private string _noteTags = "";
    [ObservableProperty] private NoteVisibility _noteVisibility = NoteVisibility.Private;
    [ObservableProperty] private string _noteStatus = "";
    [ObservableProperty] private string _noteHeader = "Select a favourite to see its notes.";
    [ObservableProperty] private string _noteMeta = "";

    public IReadOnlyList<NoteVisibility> Visibilities { get; } = [NoteVisibility.Private, NoteVisibility.Shared];

    public bool HasNoteContext => SelectedFavorite is not null;

    // ---- Import -----------------------------------------------------------

    [ObservableProperty] private string _importText = "";
    [ObservableProperty] private string _importStatus = "";

    partial void OnSelectedFavoriteChanged(FavoriteRow? value)
    {
        OnPropertyChanged(nameof(HasNoteContext));
        if (value is null)
            return;
        var note = _library.NoteFor(value.MatchUuid);
        NoteText = note?.Text ?? "";
        NoteTags = note is null ? "" : string.Join(", ", note.Tags);
        NoteVisibility = note?.Visibility ?? NoteVisibility.Private;
        NoteHeader = $"{value.PlayersLabel} — {value.MapName} ({value.VersionLabel})";
        var metaBits = new List<string>
        {
            value.AddedAt == DateTimeOffset.MinValue
                ? "★ Favourited earlier (before the launcher recorded dates)"
                : $"★ Favourited {value.AddedLabel}",
            value.IsWatched
                ? $"Watched {value.Watched!.WatchedAt.LocalDateTime:yyyy-MM-dd HH:mm} on {value.Watched.Version}."
                : "Not watched yet.",
        };
        if (value.HasDesync)
            metaBits.Add("⚠ This game desynced.");
        NoteMeta = string.Join(" · ", metaBits);
        NoteStatus = "";
    }

    partial void OnSelectedSavedListChanged(SavedReplayList? value)
    {
        if (_suppressListFieldSync || value is null)
            return;
        ListName = value.Name;
        ListTitle = value.Title;
    }

    // ---- Rebuild ----------------------------------------------------------

    /// <summary>The library changed (either tab saved something): rebuild the
    /// lists and the favourite rows.</summary>
    private void OnLibraryChanged()
    {
        RebuildLists();
        RebuildFavorites();
    }

    private void RebuildLists()
    {
        var previousId = SelectedSavedList?.Id;
        var addId = SelectedAddList?.Id;
        _suppressListFieldSync = true;
        SavedLists.Clear();
        foreach (var list in _library.Lists.OrderByDescending(l => l.SavedAt))
            SavedLists.Add(list);
        SelectedSavedList = SavedLists.FirstOrDefault(l => l.Id == previousId) ?? SavedLists.FirstOrDefault();
        SelectedAddList = SavedLists.FirstOrDefault(l => l.Id == addId);
        _suppressListFieldSync = false;
    }

    private void RebuildFavorites()
    {
        var previous = SelectedFavorite?.MatchUuid;
        var rows = new List<FavoriteRow>();
        foreach (var favorite in _library.Favorites)
        {
            var watched = _library.WatchEntry(favorite.MatchUuid);
            if (Filter == FavoritesFilter.New && watched is not null)
                continue;
            if (Filter == FavoritesFilter.Watched && watched is null)
                continue;
            var match = _online.GetValueOrDefault(favorite.MatchUuid);
            var snapshot = favorite.Snapshot;
            var players = match?.PlayersLabel ?? snapshot?.PlayersLabel ??
                          $"Replay {ShortUuid(favorite.MatchUuid)}";
            rows.Add(new FavoriteRow(
                favorite.MatchUuid,
                favorite.AddedAt,
                match?.UploadedAt ?? snapshot?.UploadedAt ?? DateTimeOffset.MinValue,
                match?.MapName ?? snapshot?.MapName ?? "?",
                players,
                match?.VersionLabel ?? snapshot?.VersionLabel ?? "?",
                match?.FileId ?? snapshot?.FileId ?? "",
                match?.HasDesync ?? snapshot?.HasDesync ?? false,
                watched,
                _library.NoteFor(favorite.MatchUuid)));
        }

        Favorites.Clear();
        foreach (var row in rows)
            Favorites.Add(row);
        SelectedFavorite = Favorites.FirstOrDefault(r =>
                r.MatchUuid.Equals(previous, StringComparison.OrdinalIgnoreCase))
            ?? Favorites.FirstOrDefault();
        FavoritesStatus = $"{rows.Count} shown · {_library.Favorites.Count} favourite(s) in total.";
    }

    private static string ShortUuid(string uuid) => uuid.Length > 8 ? uuid[..8] : uuid;

    // ---- Online enrichment ------------------------------------------------

    private async Task LoadOnlineAsync(bool force)
    {
        FavoritesBusy = true;
        try
        {
            var progress = new Progress<string>(s => FavoritesStatus = s);
            var all = await _kraken.GetMatchesAsync(force, progress, CancellationToken.None);
            _online = all.GroupBy(m => m.MatchUuid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            EnrichSnapshots();
            RebuildFavorites();
            FavoritesStatus = $"Match info refreshed from {all.Count} online game(s).";
        }
        catch (Exception ex)
        {
            FavoritesStatus = $"Could not refresh match info: {ex.Message}";
        }
        finally
        {
            FavoritesBusy = false;
        }
    }

    /// <summary>Favourites migrated from the old library (or imported without
    /// a snapshot) get their match info backfilled so they display offline.
    /// Snapshots are also refreshed when the desync flag changed online.</summary>
    private void EnrichSnapshots()
    {
        var changed = false;
        foreach (var favorite in _library.Favorites)
        {
            if (!_online.TryGetValue(favorite.MatchUuid, out var match))
                continue;
            if (favorite.Snapshot is null ||
                favorite.Snapshot.HasDesync != match.HasDesync)
            {
                favorite.Snapshot = ReplaySnapshot.From(match);
                changed = true;
            }
        }
        if (changed)
            AppRuntime.SaveLibrary();
    }

    // ---- Note commands ----------------------------------------------------

    [RelayCommand]
    private void SaveNote()
    {
        if (SelectedFavorite is null)
            return;
        var uuid = SelectedFavorite.MatchUuid;
        var tags = NoteTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _library.SaveNote(uuid, NoteText, tags, NoteVisibility);
        AppRuntime.SaveLibrary();
        NoteStatus = NoteVisibility == NoteVisibility.Shared
            ? "Saved (included when you share this note or lists)."
            : "Saved privately on this machine.";
    }

    [RelayCommand]
    private void DeleteNote()
    {
        if (SelectedFavorite is null)
            return;
        _library.DeleteNote(SelectedFavorite.MatchUuid);
        AppRuntime.SaveLibrary();
        NoteStatus = "Note deleted.";
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (SelectedFavorite is null)
            return;
        _library.ToggleFavorite(SelectedFavorite.MatchUuid);
        AppRuntime.SaveLibrary();
        NoteStatus = "Removed from favourites.";
    }

    [RelayCommand]
    private void ShareNote()
    {
        if (SelectedFavorite is null)
            return;
        var note = _library.NoteFor(SelectedFavorite.MatchUuid);
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
        var match = _online.GetValueOrDefault(SelectedFavorite.MatchUuid);
        var payload = ReplaySharing.ExportNotePayload(note, match, _library.FavoriteFor(SelectedFavorite.MatchUuid));
        var message = ReplaySharing.DiscordNoteMessage(payload, note, match);
        _ = CopyToClipboardAsync(message);
        DesktopActions.OpenDiscord();
        NoteStatus = "Copied a Discord-ready message and opened Discord — paste it into any channel. The recipient imports it in Favourites → Import.";
    }

    [RelayCommand]
    private async Task PostNoteToDiscordAsync()
    {
        if (SelectedFavorite is null)
            return;
        var note = _library.NoteFor(SelectedFavorite.MatchUuid);
        if (note is null || note.Visibility != NoteVisibility.Shared)
        {
            NoteStatus = "Save the note as Shared first.";
            return;
        }
        var match = _online.GetValueOrDefault(SelectedFavorite.MatchUuid);
        var payload = ReplaySharing.ExportNotePayload(note, match, _library.FavoriteFor(SelectedFavorite.MatchUuid));
        NoteStatus = await PostToDiscordAsync(
            ReplaySharing.DiscordNoteMessage(payload, note, match), fileName: null, fileContent: null);
    }

    // ---- Favourites share -------------------------------------------------

    [RelayCommand]
    private void ShareFavourites()
    {
        if (_library.Favorites.Count == 0)
        {
            FavoritesStatus = "Nothing favourited yet — star games on the Replays page and they appear here.";
            return;
        }
        var json = ReplaySharing.ExportFavorites(_library, _online.Values);
        var path = ReplaySharing.SaveToDownloads("Favourites", json);
        _ = CopyToClipboardAsync(json);
        FavoritesStatus = $"Shared {_library.Favorites.Count} favourite(s) → {path}. The share file is in Downloads and the payload is on the clipboard — attach the file in Discord; the recipient imports it in Favourites → Import.";
    }

    [RelayCommand]
    private async Task PostFavouritesToDiscordAsync()
    {
        if (_library.Favorites.Count == 0)
        {
            FavoritesStatus = "Nothing favourited yet.";
            return;
        }
        var json = ReplaySharing.ExportFavorites(_library, _online.Values);
        var noteCount = _library.Notes.Count(n =>
            _library.IsFavorite(n.MatchUuid) && n.Visibility == NoteVisibility.Shared);
        FavoritesStatus = await PostToDiscordAsync(
            ReplaySharing.DiscordListMessage("Favourites", "", _library.Favorites.Count, noteCount),
            "Cosmonarchy-Favourites.json", json);
    }

    // ---- Community publishing ----------------------------------------------

    [RelayCommand]
    private async Task PublishFavouritesAsync()
    {
        if (_library.Favorites.Count == 0)
        {
            FavoritesStatus = "Nothing favourited yet.";
            return;
        }
        var json = ReplaySharing.ExportFavorites(_library, _online.Values, Author());
        var noteCount = _library.Notes.Count(n =>
            _library.IsFavorite(n.MatchUuid) && n.Visibility == NoteVisibility.Shared);
        FavoritesStatus = await PublishToCommunityAsync(new CommunityShare
        {
            FileName = NewShareFileName(),
            Kind = ReplaySharing.FavoritesFormat,
            Name = "Favourites",
            SavedAt = DateTimeOffset.UtcNow,
            GameCount = _library.Favorites.Count,
            NoteCount = noteCount,
            DiscordName = AppRuntime.CurrentUser?.Name ?? "",
            DiscordId = AppRuntime.CurrentUser?.DiscordId ?? "",
        }, json);
    }

    [RelayCommand]
    private async Task PublishListAsync()
    {
        if (SelectedSavedList is not { } list)
            return;
        var json = ReplaySharing.ExportList(list, _online.Values, _library, Author());
        var noteCount = list.MatchUuids.Count(u =>
            _library.NoteFor(u) is { Visibility: NoteVisibility.Shared });
        ListStatus = await PublishToCommunityAsync(new CommunityShare
        {
            FileName = NewShareFileName(),
            Kind = ReplaySharing.ListFormat,
            Name = list.Name,
            Title = list.Title,
            SavedAt = DateTimeOffset.UtcNow,
            GameCount = list.MatchUuids.Count,
            NoteCount = noteCount,
            DiscordName = AppRuntime.CurrentUser?.Name ?? "",
            DiscordId = AppRuntime.CurrentUser?.DiscordId ?? "",
        }, json);
    }

    [RelayCommand]
    private async Task PublishNoteAsync()
    {
        if (SelectedFavorite is null)
            return;
        var note = _library.NoteFor(SelectedFavorite.MatchUuid);
        if (note is null || note.Visibility != NoteVisibility.Shared)
        {
            NoteStatus = "Save the note as Shared first.";
            return;
        }
        var match = _online.GetValueOrDefault(SelectedFavorite.MatchUuid);
        var payload = ReplaySharing.ExportNotePayload(
            note, match, _library.FavoriteFor(SelectedFavorite.MatchUuid), Author());
        var name = note.Text.Trim();
        NoteStatus = await PublishToCommunityAsync(new CommunityShare
        {
            FileName = NewShareFileName(),
            Kind = ReplaySharing.NoteFormat,
            Name = name.Length == 0 ? "Note" : name.Length > 40 ? name[..40] + "…" : name,
            SavedAt = DateTimeOffset.UtcNow,
            GameCount = 1,
            NoteCount = 1,
            DiscordName = AppRuntime.CurrentUser?.Name ?? "",
            DiscordId = AppRuntime.CurrentUser?.DiscordId ?? "",
        }, payload);
    }

    private static ReplaySharing.Author Author() =>
        AppRuntime.CurrentUser is { } user
            ? new ReplaySharing.Author(user.Name, user.DiscordId)
            : new ReplaySharing.Author(null, null);

    private static string NewShareFileName() =>
        "cosmonarchy-" + Guid.NewGuid().ToString("N")[..8] + ".json";

    private async Task<string> PublishToCommunityAsync(CommunityShare share, string payload)
    {
        if (string.IsNullOrWhiteSpace(_settings.GitHubToken))
            return "Publishing needs a GitHub token in Settings (fine-grained, “Contents: read and write” on the community repo). Without one you can still share via Discord or the share file.";
        try
        {
            await new CommunityService().PublishAsync(_settings.CommunityRepo, _settings.GitHubToken, share, payload);
            return $"Published “{share.Name}” to {_settings.CommunityRepo} — it now appears in the Community tab. Log in on the Account page to stamp your Discord name on future shares.";
        }
        catch (Exception ex)
        {
            return $"Could not publish: {ex.Message}";
        }
    }

    // ---- List commands ----------------------------------------------------

    [RelayCommand]
    private void CreateList()
    {
        var name = ListName.Trim();
        if (name.Length == 0)
        {
            ListStatus = "Name the list first.";
            return;
        }
        var list = new SavedReplayList { Name = name, Title = ListTitle.Trim() };
        _library.Lists.Add(list);
        AppRuntime.SaveLibrary();
        _suppressListFieldSync = true;
        ListName = "";
        ListTitle = "";
        SelectedSavedList = SavedLists.FirstOrDefault(l => l.Id == list.Id);
        _suppressListFieldSync = false;
        ListStatus = $"Created “{name}” — add games from the notes panel (“Add to list”).";
    }

    [RelayCommand]
    private void UpdateList()
    {
        if (SelectedSavedList is not { } list)
        {
            ListStatus = "Select a list first.";
            return;
        }
        var name = ListName.Trim();
        if (name.Length == 0)
        {
            ListStatus = "Name the list first.";
            return;
        }
        list.Name = name;
        list.Title = ListTitle.Trim();
        AppRuntime.SaveLibrary();
        ListStatus = $"Updated “{name}”.";
    }

    [RelayCommand]
    private void DeleteList()
    {
        if (SelectedSavedList is not { } list)
            return;
        _library.Lists.Remove(list);
        AppRuntime.SaveLibrary();
        ListStatus = $"Deleted “{list.Name}”.";
    }

    [RelayCommand]
    private void ShareList()
    {
        if (SelectedSavedList is not { } list)
            return;
        var json = ReplaySharing.ExportList(list, _online.Values, _library);
        var path = ReplaySharing.SaveToDownloads(list.Name, json);
        _ = CopyToClipboardAsync(json);
        ListStatus = $"Shared “{list.Name}” → {path}. The share file is in Downloads and the payload is on the clipboard — attach the file in Discord; the recipient imports it in Favourites → Import.";
    }

    [RelayCommand]
    private async Task PostListToDiscordAsync()
    {
        if (SelectedSavedList is not { } list)
            return;
        var json = ReplaySharing.ExportList(list, _online.Values, _library);
        var noteCount = list.MatchUuids.Count(u =>
            _library.NoteFor(u) is { Visibility: NoteVisibility.Shared });
        ListStatus = await PostToDiscordAsync(
            ReplaySharing.DiscordListMessage(list.Name, list.Title, list.MatchUuids.Count, noteCount),
            $"Cosmonarchy-{SafeFileName(list.Name)}.json", json);
    }

    [RelayCommand]
    private void AddSelectedToList()
    {
        if (SelectedFavorite is null || SelectedAddList is not { } list)
            return;
        var uuid = SelectedFavorite.MatchUuid;
        if (!list.MatchUuids.Any(u => u.Equals(uuid, StringComparison.OrdinalIgnoreCase)))
        {
            list.MatchUuids.Insert(0, uuid);
            AppRuntime.SaveLibrary();
        }
        ListStatus = $"Added “{SelectedFavorite.PlayersLabel}” to “{list.Name}” ({list.MatchUuids.Count} game(s)).";
    }

    [RelayCommand]
    private void RemoveSelectedFromList()
    {
        if (SelectedFavorite is null || SelectedSavedList is not { } list)
            return;
        if (list.MatchUuids.RemoveAll(u => u.Equals(SelectedFavorite.MatchUuid, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            AppRuntime.SaveLibrary();
            ListStatus = $"Removed from “{list.Name}”.";
        }
        else
        {
            ListStatus = "That game is not in the selected list.";
        }
    }

    // ---- Import -----------------------------------------------------------

    /// <summary>Imports a share payload (raw JSON, code block, or a full
    /// Discord message). Called by the paste box, the clipboard button, and
    /// the file picker in the page.</summary>
    public void Import(string? text, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ImportStatus = "Nothing to import.";
            return;
        }
        var share = ReplaySharing.ParseImport(text);
        if (share is null)
        {
            ImportStatus = "That does not look like a Cosmonarchy share (no “cosmonarchy-plus-launcher/replay-…” payload found).";
            return;
        }
        var (favourites, notes, notesUpdated, lists) = _library.Merge(share);
        AppRuntime.SaveLibrary();
        var from = string.IsNullOrWhiteSpace(source) ? "" : $" from {source}";
        var bits = new List<string>();
        if (favourites > 0)
            bits.Add($"{favourites} favourite(s)");
        if (notes > 0)
            bits.Add($"{notes} note(s)");
        if (notesUpdated > 0)
            bits.Add($"{notesUpdated} note(s) updated");
        if (lists > 0)
            bits.Add($"{lists} list(s)");
        ImportStatus = bits.Count == 0
            ? $"Recognised the share{from} but it contained nothing new."
            : $"Imported{from}: {string.Join(", ", bits)}.";
        ImportText = "";
    }

    [RelayCommand]
    private void ImportPasted() => Import(ImportText, "the paste box");

    [RelayCommand]
    private async Task ImportFromClipboardAsync()
    {
        if (AppRuntime.Clipboard is null)
        {
            ImportStatus = "Clipboard unavailable.";
            return;
        }
        Import(await AppRuntime.Clipboard.GetTextAsync(), "the clipboard");
    }

    // ---- Helpers ----------------------------------------------------------

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

    private static async Task CopyToClipboardAsync(string text)
    {
        if (AppRuntime.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private static string SafeFileName(string name)
    {
        var safe = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        safe = string.Concat(safe.Select(c => c == ' ' ? '-' : c));
        return safe.Length == 0 ? "replay-list" : safe;
    }
}
