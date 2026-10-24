using System.Text.Json.Serialization;

namespace CosmonarchyPlusLauncher.Core.Models;

/// <summary>One player slot in an uploaded replay. <paramref name="Team"/> is
/// the team array index from the replay (0-3); empty teams are omitted.</summary>
public sealed record OnlinePlayer(string Name, string Race, string Type, int? Apm, int Team = 0);

/// <summary>A replay file as listed by the ReplayKraken API
/// (<c>GET https://cosmonarchy.com/api/matches</c>), reduced to what the
/// launcher needs. One match can carry several replay files (variants); this
/// is the match's main replay. <paramref name="HasDesync"/> is true when the
/// API reports a desync file for the game — the launcher highlights it red.</summary>
public sealed record OnlineMatch(
    string MatchUuid,
    DateTimeOffset UploadedAt,
    string MapName,
    TimeSpan Duration,
    string GameVersion,
    string GameVersionKind,
    IReadOnlyList<OnlinePlayer> Players,
    string UploaderName,
    string FileId,
    bool HasDesync = false)
{
    /// <summary>"A (Zerg) vs B (Terran)" — team boundaries are not shown.</summary>
    [JsonIgnore]
    public string PlayersLabel => Players.Count == 0
        ? UploaderName
        : string.Join(" vs ", Players.Select(p => $"{p.Name} ({p.Race})"));

    [JsonIgnore]
    public string DurationLabel => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours}:{Duration.Minutes:00}:{Duration.Seconds:00}"
        : $"{Duration.Minutes}:{Duration.Seconds:00}";

    [JsonIgnore]
    public string DateLabel => UploadedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore]
    public string VersionLabel => string.IsNullOrWhiteSpace(GameVersionKind)
        ? GameVersion
        : $"{GameVersion} ({GameVersionKind})";

    [JsonIgnore]
    public string FileName =>
        $"{Sanitize(UploaderName)}-{Sanitize(GameVersion)}-{MatchUuid[..8]}.rep";

    private static string Sanitize(string value)
    {
        var chars = value.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != ' ').ToArray();
        return chars.Length == 0 ? "replay" : new string(chars);
    }
}
