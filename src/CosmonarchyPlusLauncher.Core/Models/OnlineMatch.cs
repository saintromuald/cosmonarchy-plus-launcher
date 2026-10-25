using System.Text.Json.Serialization;
using CosmonarchyPlusLauncher.Core.Services;

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
    bool HasDesync = false,
    int? WinnerTeam = null)
{
    /// <summary>Which channel the match belongs to. Matches from the
    /// self-hosted Plus server are always <see cref="ReplayChannel.Plus"/>;
    /// matches from the official list start as Original (the Replays tab
    /// refines those by SHA against the fork repositories).</summary>
    [JsonIgnore]
    public ReplayChannel Channel { get; init; } = ReplayChannel.Original;

    /// <summary>API base the match was listed by. Empty = the official
    /// ReplayKraken at cosmonarchy.com; replay downloads route through it.</summary>
    [JsonIgnore]
    public string ApiBase { get; init; } = "";

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
