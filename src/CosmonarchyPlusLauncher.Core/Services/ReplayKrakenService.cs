using System.Globalization;
using System.Net;
using System.Text.Json;
using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>ReplayKraken client: the public match list behind
/// cosmonarchy.com/matches, plus replay downloads. This is the data CosmoWatch
/// resolves deep links against; here it powers the launcher's online browser.
///
/// The list endpoint returns every uploaded match in one ~22 MB JSON document
/// (no paging parameters), so the mapped summaries are cached and refreshed on
/// demand / after <see cref="CacheTtl"/>.</summary>
public sealed class ReplayKrakenService
{
    public const string ApiBase = "https://cosmonarchy.com/api";
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly string _cacheFile;

    public ReplayKrakenService(HttpClient? http = null, string? cacheFile = null)
    {
        _http = http ?? CreateHttpClient();
        _cacheFile = cacheFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cosmonarchy-plus-launcher", "matches-cache-v3.json");
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CosmonarchyPlusLauncher");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    /// <summary>All matches, newest first. Uses the on-disk summary cache when
    /// it is younger than <see cref="CacheTtl"/> unless <paramref name="force"/>
    /// is set.</summary>
    public async Task<IReadOnlyList<OnlineMatch>> GetMatchesAsync(
        bool force = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!force)
        {
            var cached = ReadCache();
            if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < CacheTtl)
            {
                progress?.Report($"Using the match list cached {cached.FetchedAt.LocalDateTime:HH:mm}.");
                return cached.Matches;
            }
        }

        progress?.Report("Downloading the match list from cosmonarchy.com…");
        await using var stream = await _http.GetStreamAsync($"{ApiBase}/matches", ct);
        var matches = await ParseMatchesAsync(stream, ct);
        progress?.Report($"{matches.Count} matches on the server.");
        WriteCache(matches);
        return matches;
    }

    /// <summary>Latest N matches, or the latest N for one player (name match on
    /// any player slot or the uploader, case-insensitive).</summary>
    public static IReadOnlyList<OnlineMatch> Filter(
        IEnumerable<OnlineMatch> matches, int limit, string? player = null)
    {
        var query = matches;
        if (!string.IsNullOrWhiteSpace(player))
        {
            var wanted = player.Trim();
            query = query.Where(m =>
                m.UploaderName.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                m.Players.Any(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)));
        }
        return query.OrderByDescending(m => m.UploadedAt).Take(limit).ToList();
    }

    /// <summary>Download a match's main replay into <paramref name="directory"/>.
    /// Returns the saved path. Existing complete files are reused.</summary>
    public async Task<string> DownloadReplayAsync(
        OnlineMatch match, string directory, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, match.FileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            progress?.Report($"{match.FileName} is already downloaded.");
            return path;
        }

        var url = $"{ApiBase}/matches/{match.MatchUuid}/download/{match.FileId}";
        progress?.Report($"Downloading {match.FileName}…");
        await using (var source = await _http.GetStreamAsync(url, ct))
        await using (var target = File.Create(path))
            await source.CopyToAsync(target, ct);
        return path;
    }

    /// <summary>Parse the API's match array. Internal for tests.</summary>
    internal static async Task<IReadOnlyList<OnlineMatch>> ParseMatchesAsync(
        Stream json, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(json, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The match API did not return an array.");

        var matches = new List<OnlineMatch>(doc.RootElement.GetArrayLength());
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var match = Map(item);
            if (match is not null)
                matches.Add(match);
        }
        return matches;
    }

    private static OnlineMatch? Map(JsonElement item)
    {
        var uuid = GetString(item, "matchUuid");
        if (string.IsNullOrEmpty(uuid))
            return null;

        var main = item.TryGetProperty("mainReplay", out var mainEl) ? mainEl : default;
        var replay = main.ValueKind == JsonValueKind.Object ? main
            : item.TryGetProperty("replays", out var list) && list.ValueKind == JsonValueKind.Array &&
              list.GetArrayLength() > 0
                ? list[0]
                : default;
        if (replay.ValueKind != JsonValueKind.Object)
            return null;

        var info = replay.TryGetProperty("replayInfo", out var infoEl) ? infoEl : default;
        var mapName = info.ValueKind == JsonValueKind.Object ? GetString(info, "mapName") : "";
        var duration = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("gameDuration", out var d) &&
                       d.TryGetDouble(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;
        var uploader = GetString(replay, "uploaderName");
        if (string.IsNullOrEmpty(uploader) && info.ValueKind == JsonValueKind.Object)
            uploader = GetString(info, "uploaderName");

        var players = new List<OnlinePlayer>();
        if (info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("teams", out var teams) && teams.ValueKind == JsonValueKind.Array)
        {
            var teamIndex = 0;
            foreach (var team in teams.EnumerateArray())
            {
                if (team.ValueKind != JsonValueKind.Object ||
                    !team.TryGetProperty("players", out var slots) ||
                    slots.ValueKind != JsonValueKind.Array)
                {
                    teamIndex++;
                    continue;
                }
                foreach (var p in slots.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object)
                        continue;
                    var name = GetString(p, "name");
                    if (string.IsNullOrEmpty(name))
                        continue;
                    players.Add(new OnlinePlayer(
                        name,
                        GetString(p, "race"),
                        GetString(p, "type"),
                        p.TryGetProperty("apm", out var apm) && apm.TryGetInt32(out var a) ? a : null,
                        teamIndex));
                }
                teamIndex++;
            }
        }

        return new OnlineMatch(
            uuid,
            ObjectIdTime(GetString(item, "_id")),
            mapName,
            duration,
            GetString(replay, "gameVersion"),
            GetString(item, "gameVersionKind"),
            players,
            uploader,
            GetString(replay, "fileId"),
            HasDesyncFile(main) || HasDesyncFiles(item));

    /// <summary>True when the replay element carries the desync flag.</summary>
    private static bool HasDesyncFile(JsonElement replay) =>
        replay.ValueKind == JsonValueKind.Object &&
        replay.TryGetProperty("hasDesyncFile", out var flag) &&
        flag.ValueKind == JsonValueKind.True;

    /// <summary>True when any uploaded variant of the match has a desync file.</summary>
    private static bool HasDesyncFiles(JsonElement item)
    {
        if (!item.TryGetProperty("replays", out var replays) || replays.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var variant in replays.EnumerateArray())
        {
            if (HasDesyncFile(variant))
                return true;
        }
        return false;
    }

    /// <summary>Mongo ObjectIDs start with the creation time in Unix seconds;
    /// the match API exposes no other timestamp.</summary>
    internal static DateTimeOffset ObjectIdTime(string objectId)
    {
        if (objectId.Length < 8 ||
            !uint.TryParse(objectId[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.MinValue;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private sealed record Cache(DateTimeOffset FetchedAt, IReadOnlyList<OnlineMatch> Matches);

    private Cache? ReadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile))
                return null;
            var cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(_cacheFile));
            return cache?.Matches.Count > 0 ? cache : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private void WriteCache(IReadOnlyList<OnlineMatch> matches)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var cache = new Cache(DateTimeOffset.UtcNow, matches);
            File.WriteAllText(_cacheFile, JsonSerializer.Serialize(cache));
        }
        catch (IOException)
        {
            // A cache is an optimisation; never fail the list over it.
        }
    }
}
