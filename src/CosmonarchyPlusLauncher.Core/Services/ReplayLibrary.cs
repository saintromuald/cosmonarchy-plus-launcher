using System.Text.Json;
using System.Text.Json.Serialization;
using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

public enum NoteVisibility
{
    Private,
    Shared,
}

/// <summary>A note + tags attached to one replay. Private notes never leave
/// the machine (not in shared lists, not in share-note exports).</summary>
public sealed class ReplayNote
{
    public string MatchUuid { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public NoteVisibility Visibility { get; set; } = NoteVisibility.Private;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WatchedReplay
{
    public string MatchUuid { get; set; } = "";
    public string FileId { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset WatchedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>An offline summary of one match, stored with favourites so the
/// Favourites tab renders (and shares) without the online match list.</summary>
public sealed class ReplaySnapshot
{
    public DateTimeOffset UploadedAt { get; set; }
    public string MapName { get; set; } = "";
    public string PlayersLabel { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public string GameVersionKind { get; set; } = "";
    public string FileId { get; set; } = "";
    /// <summary>True when the match desynced (highlighted red).</summary>
    public bool HasDesync { get; set; }

    [JsonIgnore]
    public string DateLabel => UploadedAt == DateTimeOffset.MinValue
        ? ""
        : UploadedAt.LocalDateTime.ToString("yyyy-MM-dd");

    [JsonIgnore]
    public string VersionLabel => GameVersion.Length == 0
        ? ""
        : string.IsNullOrWhiteSpace(GameVersionKind)
            ? GameVersion
            : $"{GameVersion} ({GameVersionKind})";

    public static ReplaySnapshot From(OnlineMatch match) => new()
    {
        UploadedAt = match.UploadedAt,
        MapName = match.MapName,
        PlayersLabel = match.PlayersLabel,
        GameVersion = match.GameVersion,
        GameVersionKind = match.GameVersionKind,
        FileId = match.FileId,
        HasDesync = match.HasDesync,
    };
}

/// <summary>One favourited replay: when it was added plus a snapshot of the
/// match so the Favourites tab works offline.</summary>
public sealed class FavoriteReplay
{
    public string MatchUuid { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public ReplaySnapshot? Snapshot { get; set; }
}

/// <summary>A named, shareable collection of replays. <see cref="Name"/> is
/// the short label; <see cref="Title"/> an optional longer description.</summary>
public sealed class SavedReplayList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>Optional longer title / description, shown next to the name.</summary>
    public string Title { get; set; } = "";
    public ReplayFilters Filters { get; set; } = new();
    public List<string> MatchUuids { get; set; } = [];
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string Label => Title.Length == 0 ? Name : $"{Name} — {Title}";
}

/// <summary>What a share payload contained, ready to merge into the local
/// library. Parsed by <see cref="ReplaySharing.ParseImport"/>.</summary>
public sealed class ImportedShare
{
    public List<FavoriteReplay> Favorites { get; } = [];
    public List<ReplayNote> Notes { get; } = [];
    public List<SavedReplayList> Lists { get; } = [];
    /// <summary>Games referenced by the payload (with or without notes).</summary>
    public int GameCount { get; set; }
    /// <summary>The sharer's site/Discord name, when they were logged in.</summary>
    public string AuthorDiscordName { get; set; } = "";
}

/// <summary>The user's replay library: what was watched, favourites with the
/// date they were added and a match snapshot, notes with tags and visibility,
/// and saved lists.</summary>
public sealed class ReplayLibrary
{
    public int Version { get; set; } = 2;
    public List<WatchedReplay> Watched { get; set; } = [];
    public List<FavoriteReplay> Favorites { get; set; } = [];
    public List<ReplayNote> Notes { get; set; } = [];
    public List<SavedReplayList> Lists { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<SavedReplayList> SavedLists => Lists;

    public bool IsFavorite(string matchUuid) => FavoriteFor(matchUuid) is not null;

    public FavoriteReplay? FavoriteFor(string matchUuid) =>
        Favorites.FirstOrDefault(f => f.MatchUuid.Equals(matchUuid, StringComparison.OrdinalIgnoreCase));

    public DateTimeOffset? FavoriteAddedAt(string matchUuid) => FavoriteFor(matchUuid)?.AddedAt;

    /// <summary>Returns true when the replay is now a favourite. New favourites
    /// record the add time and a snapshot for offline display.</summary>
    public bool ToggleFavorite(string matchUuid, ReplaySnapshot? snapshot = null)
    {
        var existing = FavoriteFor(matchUuid);
        if (existing is not null)
        {
            Favorites.Remove(existing);
            return false;
        }
        AddFavorite(matchUuid, snapshot);
        return true;
    }

    /// <summary>Adds a favourite without touching an existing one.</summary>
    public void AddFavorite(string matchUuid, ReplaySnapshot? snapshot = null, DateTimeOffset? addedAt = null)
    {
        if (FavoriteFor(matchUuid) is not null)
            return;
        Favorites.Insert(0, new FavoriteReplay
        {
            MatchUuid = matchUuid,
            AddedAt = addedAt ?? DateTimeOffset.UtcNow,
            Snapshot = snapshot,
        });
    }

    public WatchedReplay? WatchEntry(string matchUuid) =>
        Watched.FirstOrDefault(w => w.MatchUuid.Equals(matchUuid, StringComparison.OrdinalIgnoreCase));

    public bool WasWatched(string matchUuid) => WatchEntry(matchUuid) is not null;

    public void MarkWatched(string matchUuid, string fileId, string version)
    {
        Watched.RemoveAll(w => w.MatchUuid.Equals(matchUuid, StringComparison.OrdinalIgnoreCase));
        Watched.Insert(0, new WatchedReplay
        {
            MatchUuid = matchUuid,
            FileId = fileId,
            Version = version,
            WatchedAt = DateTimeOffset.UtcNow,
        });
        if (Watched.Count > 2000)
            Watched.RemoveRange(2000, Watched.Count - 2000);
    }

    public ReplayNote? NoteFor(string matchUuid) =>
        Notes.FirstOrDefault(n => n.MatchUuid.Equals(matchUuid, StringComparison.OrdinalIgnoreCase));

    public void SaveNote(string matchUuid, string text, IEnumerable<string> tags, NoteVisibility visibility)
    {
        var note = NoteFor(matchUuid);
        var cleaned = tags.Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(text) && cleaned.Count == 0)
        {
            DeleteNote(matchUuid);
            return;
        }
        if (note is null)
        {
            note = new ReplayNote { MatchUuid = matchUuid };
            Notes.Add(note);
        }
        note.Text = text.Trim();
        note.Tags = cleaned;
        note.Visibility = visibility;
        note.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void DeleteNote(string matchUuid)
    {
        if (NoteFor(matchUuid) is { } note)
            Notes.Remove(note);
    }

    /// <summary>Merges an imported share: favourites missing locally are added
    /// (existing ones keep their original add date), notes are added or
    /// replaced when the incoming note is newer, and lists replace any local
    /// list with the same id.</summary>
    public (int FavoritesAdded, int NotesAdded, int NotesUpdated, int ListsAdded) Merge(ImportedShare share)
    {
        var favouritesAdded = 0;
        foreach (var favorite in share.Favorites)
        {
            if (FavoriteFor(favorite.MatchUuid) is null)
            {
                AddFavorite(favorite.MatchUuid, favorite.Snapshot, favorite.AddedAt);
                favouritesAdded++;
            }
        }

        var notesAdded = 0;
        var notesUpdated = 0;
        foreach (var note in share.Notes)
        {
            var existing = NoteFor(note.MatchUuid);
            if (existing is null)
            {
                Notes.Add(note);
                notesAdded++;
            }
            else if (note.UpdatedAt > existing.UpdatedAt)
            {
                Notes.Remove(existing);
                Notes.Add(note);
                notesUpdated++;
            }
        }

        var listsAdded = 0;
        foreach (var list in share.Lists)
        {
            Lists.RemoveAll(l => l.Id.Equals(list.Id, StringComparison.OrdinalIgnoreCase));
            Lists.Add(list);
            listsAdded++;
        }

        return (favouritesAdded, notesAdded, notesUpdated, listsAdded);
    }
}

/// <summary>Loads and saves the library next to the match cache. Version 1
/// files (favourites were a plain uuid list) are migrated: the favourites are
/// kept, with an unknown add date and no snapshot.</summary>
public sealed class ReplayLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ReplayLibraryStore(string? path = null) =>
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cosmonarchy-plus-launcher", "library.json");

    public string Path { get; }

    public ReplayLibrary Load()
    {
        try
        {
            if (!File.Exists(Path))
                return new ReplayLibrary();
            var text = File.ReadAllText(Path);
            try
            {
                return JsonSerializer.Deserialize<ReplayLibrary>(text) ?? new ReplayLibrary();
            }
            catch (JsonException)
            {
                return MigrateV1(text);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new ReplayLibrary();
        }
    }

    public void Save(ReplayLibrary library)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(library, JsonOptions));
    }

    private static ReplayLibrary MigrateV1(string text)
    {
        var v1 = JsonSerializer.Deserialize<LibraryV1>(text);
        if (v1 is null)
            return new ReplayLibrary();
        return new ReplayLibrary
        {
            Watched = v1.Watched,
            // The old format stored no add dates: mark them as unknown instead
            // of inventing one. The UI shows "earlier" for these.
            Favorites = v1.Favorites.Select(f => new FavoriteReplay
            {
                MatchUuid = f,
                AddedAt = DateTimeOffset.MinValue,
            }).ToList(),
            Notes = v1.Notes,
            Lists = v1.Lists,
        };
    }

    /// <summary>The pre-2 shape of the file: favourites were plain strings.</summary>
    private sealed class LibraryV1
    {
        public int Version { get; set; } = 1;
        public List<WatchedReplay> Watched { get; set; } = [];
        public List<string> Favorites { get; set; } = [];
        public List<ReplayNote> Notes { get; set; } = [];
        public List<SavedReplayList> Lists { get; set; } = [];
    }
}
