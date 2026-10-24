using System.Text.Json;
using System.Text.Json.Serialization;
using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Shareable exports: lists of replays with their tags and any notes
/// the user marked shared. Private notes are never included.</summary>
public static class ReplaySharing
{
    public const string ListFormat = "cosmonarchy-plus-launcher/replay-list";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ExportList(
        SavedReplayList list, IEnumerable<OnlineMatch> allMatches, ReplayLibrary library)
    {
        var byUuid = allMatches
            .GroupBy(m => m.MatchUuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var games = new List<object>();
        foreach (var uuid in list.MatchUuids)
        {
            if (!byUuid.TryGetValue(uuid, out var match))
                continue;
            var note = library.NoteFor(uuid);
            games.Add(new
            {
                matchUuid = match.MatchUuid,
                date = match.DateLabel,
                map = match.MapName,
                players = match.PlayersLabel,
                version = match.VersionLabel,
                tags = note is { Visibility: NoteVisibility.Shared, Tags.Count: > 0 } ? note.Tags : null,
                note = note is { Visibility: NoteVisibility.Shared, Text.Length: > 0 } ? note.Text : null,
            });
        }

        var payload = new
        {
            format = ListFormat,
            version = 1,
            name = list.Name,
            savedAt = list.SavedAt.ToString("O"),
            filters = new
            {
                racePairs = list.Filters.RacePairs.OrderBy(p => p).ToList(),
                teamSizes = list.Filters.TeamSizes.OrderBy(t => t).ToList(),
                list.Filters.IgnoreObservers,
            },
            games,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>Plain-text note for the clipboard. Null when the note is
    /// private (callers show "private - not shared").</summary>
    public static string? ExportNote(ReplayNote note, OnlineMatch? match)
    {
        if (note.Visibility != NoteVisibility.Shared)
            return null;
        var tags = note.Tags.Count > 0 ? $" [{string.Join(", ", note.Tags)}]" : "";
        var context = match is null
            ? note.MatchUuid
            : $"{match.DateLabel} - {match.PlayersLabel} on {match.MapName} ({match.VersionLabel})";
        return $"{context}{tags}\n{note.Text}";
    }

    /// <summary>Write a share file to the user's Downloads folder and return
    /// its path.</summary>
    public static string SaveToDownloads(string name, string content)
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var safe = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != ' '));
        if (safe.Length == 0)
            safe = "replay-list";
        var path = Path.Combine(downloads, $"Cosmonarchy-{safe}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
