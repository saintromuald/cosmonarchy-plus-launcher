using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Shareable exports and imports: notes, favourite lists and saved
/// lists as JSON payloads that another launcher can import, plus
/// Discord-ready messages. Private notes are never included anywhere.</summary>
public static class ReplaySharing
{
    public const string ListFormat = "cosmonarchy-plus-launcher/replay-list";
    public const string NoteFormat = "cosmonarchy-plus-launcher/replay-note";
    public const string FavoritesFormat = "cosmonarchy-plus-launcher/replay-favorites";

    /// <summary>Common prefix of every payload; used to find payloads inside
    /// pasted Discord messages.</summary>
    public const string FormatMarker = "cosmonarchy-plus-launcher/replay-";

    /// <summary>Discord message content limit. Messages are built to fit.</summary>
    public const int DiscordMaxLength = 2000;

    private static readonly JsonSerializerOptions Compact = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The sharer's identity, stamped on payloads when they are
    /// logged in. Community listings show <see cref="DiscordName"/>.</summary>
    public sealed record Author(string? DiscordName, string? DiscordId);

    // ---- Exports -----------------------------------------------------------

    /// <summary>Compact single-line payload for one shared note, including the
    /// match summary and the favourite state. Small enough for a Discord code
    /// block.</summary>
    public static string ExportNotePayload(ReplayNote note, OnlineMatch? match, FavoriteReplay? favorite, Author? author = null)
    {
        var payload = new
        {
            format = NoteFormat,
            version = 1,
            sharedAt = DateTimeOffset.UtcNow.ToString("O"),
            author = AuthorPayload(author),
            note = new
            {
                text = note.Text,
                tags = note.Tags,
                visibility = note.Visibility.ToString(),
                updatedAt = note.UpdatedAt.ToString("O"),
            },
            match = match is null
                ? new { matchUuid = note.MatchUuid }
                : GamePayload(match),
            favorite = favorite is not null,
            favoriteAddedAt = favorite?.AddedAt.ToString("O"),
        };
        return JsonSerializer.Serialize(payload, Compact);
    }

    /// <summary>Exports a saved list: every game (online info or the stored
    /// favourite snapshot when the game is not in the list), its shared
    /// note/tags and favourite flags, plus the list's name, title and filters.</summary>
    public static string ExportList(
        SavedReplayList list, IEnumerable<OnlineMatch> allMatches, ReplayLibrary library, Author? author = null)
    {
        var byUuid = IndexMatches(allMatches);
        var games = new List<object>();
        foreach (var uuid in list.MatchUuids)
        {
            byUuid.TryGetValue(uuid, out var match);
            games.Add(GamePayload(uuid, match, library.FavoriteFor(uuid)?.Snapshot, library.FavoriteFor(uuid), library.NoteFor(uuid)));
        }

        var payload = new
        {
            format = ListFormat,
            version = 2,
            name = list.Name,
            title = list.Title,
            savedAt = list.SavedAt.ToString("O"),
            author = AuthorPayload(author),
            filters = new
            {
                racePairs = list.Filters.RacePairs.OrderBy(p => p).ToList(),
                teamSizes = list.Filters.TeamSizes.OrderBy(t => t).ToList(),
                list.Filters.IgnoreObservers,
            },
            games,
        };
        return JsonSerializer.Serialize(payload, Pretty);
    }

    /// <summary>Exports every favourite with its add date, snapshot, and any
    /// shared note/tags.</summary>
    public static string ExportFavorites(ReplayLibrary library, IEnumerable<OnlineMatch> allMatches, Author? author = null)
    {
        var byUuid = IndexMatches(allMatches);
        var games = new List<object>();
        foreach (var favorite in library.Favorites)
        {
            byUuid.TryGetValue(favorite.MatchUuid, out var match);
            games.Add(GamePayload(favorite.MatchUuid, match, favorite.Snapshot, favorite, library.NoteFor(favorite.MatchUuid)));
        }

        var payload = new
        {
            format = FavoritesFormat,
            version = 1,
            sharedAt = DateTimeOffset.UtcNow.ToString("O"),
            name = "Favourites",
            author = AuthorPayload(author),
            games,
        };
        return JsonSerializer.Serialize(payload, Pretty);
    }

    private static object? AuthorPayload(Author? author) =>
        author?.DiscordName is null && author?.DiscordId is null
            ? null
            : new { discordName = author?.DiscordName, discordId = author?.DiscordId };

    /// <summary>One game inside a list/favourites payload. Notes and tags are
    /// only included when the note is shared.</summary>
    private static object GamePayload(
        string uuid, OnlineMatch? match, ReplaySnapshot? snapshot,
        FavoriteReplay? favorite, ReplayNote? note) => new
    {
        matchUuid = uuid,
        uploadedAt = (match?.UploadedAt ?? snapshot?.UploadedAt)?.ToString("O"),
        date = match?.DateLabel ?? snapshot?.DateLabel,
        map = match?.MapName ?? snapshot?.MapName,
        players = match?.PlayersLabel ?? snapshot?.PlayersLabel,
        version = match?.VersionLabel ?? snapshot?.VersionLabel,
        gameVersion = match?.GameVersion ?? snapshot?.GameVersion,
        gameVersionKind = match?.GameVersionKind ?? snapshot?.GameVersionKind,
        fileId = match?.FileId ?? snapshot?.FileId,
        desynced = match?.HasDesync ?? snapshot?.HasDesync ?? false,
        favorite = favorite is not null,
        favoriteAddedAt = favorite?.AddedAt.ToString("O"),
        tags = note is { Visibility: NoteVisibility.Shared, Tags.Count: > 0 } ? note.Tags : null,
        note = note is { Visibility: NoteVisibility.Shared, Text.Length: > 0 } ? note.Text : null,
    };

    private static object GamePayload(OnlineMatch match) => new
    {
        matchUuid = match.MatchUuid,
        uploadedAt = match.UploadedAt.ToString("O"),
        date = match.DateLabel,
        map = match.MapName,
        players = match.PlayersLabel,
        version = match.VersionLabel,
        gameVersion = match.GameVersion,
        gameVersionKind = match.GameVersionKind,
        fileId = match.FileId,
        desynced = match.HasDesync,
    };

    private static Dictionary<string, OnlineMatch> IndexMatches(IEnumerable<OnlineMatch> matches) =>
        matches.GroupBy(m => m.MatchUuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    // ---- Discord-ready messages -------------------------------------------

    /// <summary>A paste-ready Discord message for one note: the context, the
    /// note text and tags, and the import payload in a copyable code block.
    /// Always within Discord's message length limit.</summary>
    public static string DiscordNoteMessage(string payload, ReplayNote note, OnlineMatch? match)
    {
        var shortUuid = note.MatchUuid.Length > 8 ? note.MatchUuid[..8] : note.MatchUuid;
        var context = match is null
            ? $"replay {shortUuid}"
            : $"{match.DateLabel} — {match.PlayersLabel} on {match.MapName} ({match.VersionLabel})";
        var tags = note.Tags.Count > 0 ? $" · {string.Join(", ", note.Tags)}" : "";
        var header = $"**Cosmonarchy note** — {context}{tags}\n";
        var footer = $"```cosmonarchy\n{payload}\n```\nImport it in the launcher: Favourites → Import.";
        var budget = DiscordMaxLength - header.Length - footer.Length;
        var body = note.Text;
        if (body.Length > budget)
            body = budget > 1 ? body[..(budget - 1)] + "…" : "";
        return header + (body.Length > 0 ? body + "\n" : "") + footer;
    }

    /// <summary>A short Discord message for a list share, posted together
    /// with the payload file.</summary>
    public static string DiscordListMessage(string name, string title, int gameCount, int noteCount)
    {
        var heading = string.IsNullOrWhiteSpace(title)
            ? $"**Cosmonarchy list “{name}”**"
            : $"**Cosmonarchy list “{name}”** — {title}";
        return $"{heading}\n{gameCount} game(s), {noteCount} note(s). Import the attached .json in the launcher: Favourites → Import.";
    }

    // ---- Import ------------------------------------------------------------

    /// <summary>Parses a share payload, wherever it came from: raw JSON,
    /// JSON inside a code fence, or a full Discord message containing the
    /// payload. Null when the text holds no recognisable payload.</summary>
    public static ImportedShare? ParseImport(string text)
    {
        var json = ExtractPayload(text);
        if (json is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            return Get(root, "format") switch
            {
                NoteFormat => ParseNote(root),
                ListFormat => ParseGamesPayload(root, isList: true),
                FavoritesFormat => ParseGamesPayload(root, isList: false),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Finds the payload JSON object inside arbitrary pasted text:
    /// locates the format marker, then the enclosing braces. Tries smaller
    /// candidates when the trailing text contains stray braces.</summary>
    private static string? ExtractPayload(string text)
    {
        var marker = text.IndexOf(FormatMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return null;
        var start = text.LastIndexOf('{', marker);
        if (start < 0)
            return null;
        for (var end = text.LastIndexOf('}'); end > start; end = text.LastIndexOf('}', end - 1))
        {
            var candidate = text[start..(end + 1)];
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch (JsonException)
            {
                // Try the next closing brace.
            }
        }
        return null;
    }

    private static ImportedShare? ParseNote(JsonElement root)
    {
        var share = new ImportedShare();
        JsonElement match = default;
        TryProp(root, "match", out match);
        var uuid = Get(match, "matchUuid");
        if (uuid.Length == 0)
            return null;

        if (TryProp(root, "note", out var note))
        {
            var text = Get(note, "text");
            var tags = GetArray(note, "tags");
            if (text.Length > 0 || tags.Count > 0)
            {
                share.Notes.Add(new ReplayNote
                {
                    MatchUuid = uuid,
                    Text = text,
                    Tags = tags,
                    Visibility = Get(note, "visibility").Equals(nameof(NoteVisibility.Shared), StringComparison.OrdinalIgnoreCase)
                        ? NoteVisibility.Shared
                        : NoteVisibility.Private,
                    UpdatedAt = ParseDate(Get(note, "updatedAt")) ?? DateTimeOffset.UtcNow,
                });
            }
        }

        if (TryBool(root, "favorite", out var favorite) && favorite)
        {
            share.Favorites.Add(new FavoriteReplay
            {
                MatchUuid = uuid,
                AddedAt = ParseDate(Get(root, "favoriteAddedAt")) ?? DateTimeOffset.UtcNow,
                Snapshot = SnapshotFrom(match),
            });
        }

        share.AuthorDiscordName = GetAuthorName(root);
        share.GameCount = 1;
        return share;
    }

    private static ImportedShare? ParseGamesPayload(JsonElement root, bool isList)
    {
        var share = new ImportedShare();
        if (!TryProp(root, "games", out var games) || games.ValueKind != JsonValueKind.Array)
            return null;

        var uuids = new List<string>();
        foreach (var game in games.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object)
                continue;
            var uuid = Get(game, "matchUuid");
            if (uuid.Length == 0)
                continue;
            uuids.Add(uuid);

            if (TryBool(game, "favorite", out var favorite) && favorite)
            {
                share.Favorites.Add(new FavoriteReplay
                {
                    MatchUuid = uuid,
                    AddedAt = ParseDate(Get(game, "favoriteAddedAt")) ?? DateTimeOffset.UtcNow,
                    Snapshot = SnapshotFrom(game),
                });
            }

            var text = Get(game, "note");
            var tags = GetArray(game, "tags");
            if (text.Length > 0 || tags.Count > 0)
            {
                share.Notes.Add(new ReplayNote
                {
                    MatchUuid = uuid,
                    Text = text,
                    Tags = tags,
                    Visibility = NoteVisibility.Shared,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        if (isList)
        {
            var name = Get(root, "name");
            var list = new SavedReplayList
            {
                Name = name.Length > 0 ? name : "Imported list",
                Title = Get(root, "title"),
                MatchUuids = uuids,
                SavedAt = ParseDate(Get(root, "savedAt")) ?? DateTimeOffset.UtcNow,
            };
            if (TryProp(root, "filters", out var filters))
            {
                list.Filters.RacePairs = new HashSet<string>(GetArray(filters, "racePairs"), StringComparer.OrdinalIgnoreCase);
                list.Filters.TeamSizes = new HashSet<string>(GetArray(filters, "teamSizes"), StringComparer.OrdinalIgnoreCase);
                list.Filters.IgnoreObservers = !TryBool(filters, "ignoreObservers", out var io) || io;
            }
            share.Lists.Add(list);
        }

        share.AuthorDiscordName = GetAuthorName(root);
        share.GameCount = uuids.Count;
        return share;
    }

    private static string GetAuthorName(JsonElement root) =>
        TryProp(root, "author", out var author) ? Get(author, "discordName") : "";

    private static ReplaySnapshot? SnapshotFrom(JsonElement game)
    {
        var uploadedAt = ParseDate(Get(game, "uploadedAt"));
        var map = Get(game, "map");
        var players = Get(game, "players");
        var gameVersion = Get(game, "gameVersion");
        if (uploadedAt is null && map.Length == 0 && players.Length == 0 && gameVersion.Length == 0)
            return null;
        return new ReplaySnapshot
        {
            UploadedAt = uploadedAt ?? DateTimeOffset.MinValue,
            MapName = map,
            PlayersLabel = players,
            GameVersion = gameVersion,
            GameVersionKind = Get(game, "gameVersionKind"),
            FileId = Get(game, "fileId"),
            HasDesync = TryBool(game, "desynced", out var desynced) && desynced,
        };
    }

    // ---- Small JSON helpers ------------------------------------------------

    private static bool TryProp(JsonElement element, string name, out JsonElement value) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);

    private static string Get(JsonElement element, string name) =>
        TryProp(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static List<string> GetArray(JsonElement element, string name)
    {
        if (!TryProp(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                result.Add(s);
        }
        return result;
    }

    private static bool TryBool(JsonElement element, string name, out bool result)
    {
        result = false;
        return TryProp(element, name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static DateTimeOffset? ParseDate(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : null;

    /// <summary>Write a share file to the user's Downloads folder and return
    /// its path.</summary>
    public static string SaveToDownloads(string name, string content)
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var safe = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        safe = string.Concat(safe.Select(c => c == ' ' ? '-' : c));
        if (safe.Length == 0)
            safe = "replay-list";
        var path = Path.Combine(downloads, $"Cosmonarchy-{safe}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
