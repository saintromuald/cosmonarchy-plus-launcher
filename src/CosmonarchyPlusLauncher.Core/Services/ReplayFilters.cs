using System.Text.Json.Serialization;
using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Filters shared by both online replay tabs. Serialised verbatim in
/// saved lists, so keep it plain data (no UI types).</summary>
public sealed class ReplayFilters
{
    /// <summary>Race matchups, canonical keys from <see cref="ReplayQuery.PairKey"/>
    /// (e.g. "Terran|Zerg"). Empty = every matchup.</summary>
    public HashSet<string> RacePairs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Team sizes: "1v1", "2v2", "3v3", "4v4", "Other". Empty = all.</summary>
    public HashSet<string> TeamSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drop non-playing slots (Computers, Neutral, Rescue Passive)
    /// before counting teams and matchups. On by default.</summary>
    public bool IgnoreObservers { get; set; } = true;

    public bool FavoritesOnly { get; set; }

    public bool WatchedOnly { get; set; }

    public bool WithNotesOnly { get; set; }

    [JsonIgnore]
    public bool IsDefault =>
        RacePairs.Count == 0 && TeamSizes.Count == 0 && !FavoritesOnly && !WatchedOnly &&
        !WithNotesOnly;

    public ReplayFilters Clone() => new()
    {
        RacePairs = new HashSet<string>(RacePairs, StringComparer.OrdinalIgnoreCase),
        TeamSizes = new HashSet<string>(TeamSizes, StringComparer.OrdinalIgnoreCase),
        IgnoreObservers = IgnoreObservers,
        FavoritesOnly = FavoritesOnly,
        WatchedOnly = WatchedOnly,
        WithNotesOnly = WithNotesOnly,
    };
}

/// <summary>Team-size, race-matchup and library filtering over the ReplayKraken
/// list. Pure functions so both the UI and tests agree.</summary>
public static class ReplayQuery
{
    public static readonly IReadOnlyList<string> Races = ["Terran", "Protoss", "Zerg", "Askosi", "Other"];

    public const string OtherRace = "Other";

    public static string RaceKey(string race) =>
        Races.Any(r => r.Equals(race, StringComparison.OrdinalIgnoreCase) && r != OtherRace)
            ? Races.First(r => r.Equals(race, StringComparison.OrdinalIgnoreCase))
            : OtherRace;

    /// <summary>Canonical, order-independent pair key ("Terran|Zerg").</summary>
    public static string PairKey(string raceA, string raceB)
    {
        var a = RaceKey(raceA);
        var b = RaceKey(raceB);
        var ai = Races.ToList().IndexOf(a);
        var bi = Races.ToList().IndexOf(b);
        return ai <= bi ? $"{a}|{b}" : $"{b}|{a}";
    }

    public static bool IsObserver(OnlinePlayer player) =>
        !player.Type.Equals("Human", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<OnlinePlayer> Participants(OnlineMatch match, bool ignoreObservers) =>
        ignoreObservers ? match.Players.Where(p => !IsObserver(p)).ToList() : match.Players;

    /// <summary>"1v1" … "4v4" when exactly two equal teams; "Other" for FFA,
    /// uneven or larger games.</summary>
    public static string TeamSize(OnlineMatch match, bool ignoreObservers)
    {
        var sizes = Participants(match, ignoreObservers)
            .GroupBy(p => p.Team)
            .Select(g => g.Count())
            .OrderBy(n => n)
            .ToList();
        if (sizes.Count == 2 && sizes[0] == sizes[1] && sizes[0] is >= 1 and <= 4)
            return sizes[0] switch
            {
                1 => "1v1",
                2 => "2v2",
                3 => "3v3",
                4 => "4v4",
                _ => "Other",
            };
        return "Other";
    }

    /// <summary>True when two players on opposing teams fit one of the ticked
    /// race pairs. An empty set means no race restriction.</summary>
    public static bool MatchesRacePairs(OnlineMatch match, ISet<string> pairs, bool ignoreObservers)
    {
        if (pairs.Count == 0)
            return true;
        var players = Participants(match, ignoreObservers);
        for (var i = 0; i < players.Count; i++)
        {
            for (var j = i + 1; j < players.Count; j++)
            {
                if (players[i].Team == players[j].Team)
                    continue;
                if (pairs.Contains(PairKey(players[i].Race, players[j].Race)))
                    return true;
            }
        }
        return false;
    }

    public static IEnumerable<OnlineMatch> Apply(
        IEnumerable<OnlineMatch> matches, ReplayFilters filters, ReplayLibrary library)
    {
        var favourites = library.Favorites.Select(f => f.MatchUuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var watched = library.Watched.Select(w => w.MatchUuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var noted = library.Notes.Where(n => n.Text.Length > 0)
            .Select(n => n.MatchUuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches)
        {
            if (filters.FavoritesOnly && !favourites.Contains(match.MatchUuid))
                continue;
            if (filters.WatchedOnly && !watched.Contains(match.MatchUuid))
                continue;
            if (filters.WithNotesOnly && !noted.Contains(match.MatchUuid))
                continue;
            if (filters.TeamSizes.Count > 0 &&
                !filters.TeamSizes.Contains(TeamSize(match, filters.IgnoreObservers)))
                continue;
            if (!MatchesRacePairs(match, filters.RacePairs, filters.IgnoreObservers))
                continue;
            yield return match;
        }
    }
}
