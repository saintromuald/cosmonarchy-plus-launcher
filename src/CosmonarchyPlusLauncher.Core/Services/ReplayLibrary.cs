using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>A named, shareable snapshot of the filter plus the replays it
/// matched when saved. Applying it restores the filter (live results).</summary>
public sealed class SavedReplayList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public ReplayFilters Filters { get; set; } = new();
    public List<string> MatchUuids { get; set; } = [];
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The user's replay library: what was watched, favourites, notes
/// with tags and visibility, and saved lists.</summary>
public sealed class ReplayLibrary
{
    public int Version { get; set; } = 1;
    public List<WatchedReplay> Watched { get; set; } = [];
    public List<string> Favorites { get; set; } = [];
    public List<ReplayNote> Notes { get; set; } = [];
    public List<SavedReplayList> Lists { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<SavedReplayList> SavedLists => Lists;

    public bool IsFavorite(string matchUuid) =>
        Favorites.Any(f => f.Equals(matchUuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns true when the replay is now a favourite.</summary>
    public bool ToggleFavorite(string matchUuid)
    {
        var existing = Favorites.FirstOrDefault(f =>
            f.Equals(matchUuid, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Favorites.Remove(existing);
            return false;
        }
        Favorites.Add(matchUuid);
        return true;
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
}

/// <summary>Loads and saves the library next to the match cache.</summary>
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
            return JsonSerializer.Deserialize<ReplayLibrary>(File.ReadAllText(Path)) ?? new ReplayLibrary();
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
}
