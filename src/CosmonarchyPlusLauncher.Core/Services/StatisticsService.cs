using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Turns the ReplayKraken match list into per-player statistics:
/// races played, race-vs-race matchups, results, opponent records, and the
/// top ten most-played opponents. Purely functional — no I/O — so the same
/// code serves the UI today and the server aggregation later.</summary>
public static class StatisticsService
{
    /// <summary>Team index ReplayKraken uses when the winner was not recorded.</summary>
    public const int UnknownWinnerTeam = 4;

    /// <summary>Matches the claimed name appears in (player slot or uploader),
    /// case-insensitive.</summary>
    public static IReadOnlyList<OnlineMatch> MatchesForPlayer(
        IEnumerable<OnlineMatch> matches, string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return [];
        var wanted = playerName.Trim();
        return matches.Where(m =>
                m.UploaderName.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                m.Players.Any(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => m.UploadedAt)
            .ToList();
    }

    /// <summary>Full profile for the claimed name. <paramref name="certified"/>
    /// holds the account's certified match UUIDs; the filter selects which
    /// slice of the history feeds the numbers.</summary>
    public static PlayerStats Compute(
        IEnumerable<OnlineMatch> matches,
        string playerName,
        IReadOnlySet<string> certified,
        CertificationFilter filter = CertificationFilter.All)
    {
        var entries = BuildEntries(matches, playerName, certified, filter);
        return Aggregate(entries);
    }

    /// <summary>One row per match the claimed name appears in, with the
    /// account's team, race, result and the human opponents faced.</summary>
    public static IReadOnlyList<PlayerMatchEntry> BuildEntries(
        IEnumerable<OnlineMatch> matches,
        string playerName,
        IReadOnlySet<string> certified,
        CertificationFilter filter = CertificationFilter.All)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return [];
        var wanted = playerName.Trim();
        var result = new List<PlayerMatchEntry>();

        foreach (var match in matches)
        {
            var mine = match.Players
                .Where(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (mine.Count == 0 && !match.UploaderName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            var isCertified = certified.Contains(match.MatchUuid);
            if (filter == CertificationFilter.Certified && !isCertified)
                continue;
            if (filter == CertificationFilter.Uncertified && isCertified)
                continue;

            // Mirror match (same name on both sides) or uploader-only sighting:
            // no single team to score, so the game counts but has no result.
            var myTeams = mine.Select(p => p.Team).Distinct().ToList();
            var myRace = mine.FirstOrDefault()?.Race ?? "";
            var outcome = myTeams.Count == 1
                ? MatchOutcome(myTeams[0], match.WinnerTeam)
                : MatchResult.NoResult;

            var opponents = match.Players
                .Where(p => !p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                .Where(p => !p.Type.Equals("Computer", StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            result.Add(new PlayerMatchEntry(
                match,
                myTeams.Count == 1 ? myTeams[0] : -1,
                myRace,
                outcome,
                isCertified,
                opponents));
        }

        return result.OrderByDescending(e => e.Match.UploadedAt).ToList();
    }

    /// <summary>Win / loss / no-result for the team <paramref name="team"/>.
    /// ReplayKraken reports 0-3 for the winning team and 4 when the winner
    /// was not recorded; a missing value counts as no result too.</summary>
    internal static MatchResult MatchOutcome(int team, int? winnerTeam)
    {
        if (winnerTeam is null || winnerTeam == UnknownWinnerTeam)
            return MatchResult.NoResult;
        return winnerTeam == team ? MatchResult.Win : MatchResult.Loss;
    }

    /// <summary>Aggregates entries into the profile: races, matchups, opponents.</summary>
    public static PlayerStats Aggregate(IReadOnlyList<PlayerMatchEntry> entries)
    {
        if (entries.Count == 0)
            return PlayerStats.Empty;

        var wins = entries.Count(e => e.Result == MatchResult.Win);
        var losses = entries.Count(e => e.Result == MatchResult.Loss);
        var noResult = entries.Count - wins - losses;
        var certified = entries.Count(e => e.Certified);

        var races = entries
            .GroupBy(e => NonEmpty(e.MyRace), StringComparer.OrdinalIgnoreCase)
            .Select(g => new RaceStats(
                g.Key,
                g.Count(),
                g.Count(e => e.Result == MatchResult.Win),
                g.Count(e => e.Result == MatchResult.Loss)))
            .OrderByDescending(r => r.Games)
            .ToList();

        var matchupGames = new Dictionary<string, (int Games, int Wins, int Losses)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var myRace = NonEmpty(entry.MyRace);
            foreach (var opponent in entry.Opponents)
            {
                var oppRace = NonEmpty(opponent.Race);
                var key = $"{myRace}\u0000{oppRace}";
                matchupGames.TryGetValue(key, out var cell);
                cell.Games++;
                if (entry.Result == MatchResult.Win)
                    cell.Wins++;
                else if (entry.Result == MatchResult.Loss)
                    cell.Losses++;
                matchupGames[key] = cell;
            }
        }
        var matchups = matchupGames
            .Select(kv => new MatchupStats(
                kv.Key.Split('\u0000')[0],
                kv.Key.Split('\u0000')[1],
                kv.Value.Games, kv.Value.Wins, kv.Value.Losses))
            .OrderByDescending(m => m.Games)
            .ToList();

        // Opponent records: total games against each name and the matchup
        // they played most often.
        var perOpponent = new Dictionary<string, (int Games, int Wins, int Losses, Dictionary<string, int> Matchups)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var myRace = NonEmpty(entry.MyRace);
            foreach (var opponent in entry.Opponents)
            {
                var key = opponent.Name;
                perOpponent.TryGetValue(key, out var rec);
                rec.Matchups ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                rec.Games++;
                if (entry.Result == MatchResult.Win)
                    rec.Wins++;
                else if (entry.Result == MatchResult.Loss)
                    rec.Losses++;
                var matchupKey = $"{myRace} vs {NonEmpty(opponent.Race)}";
                rec.Matchups.TryGetValue(matchupKey, out var mGames);
                rec.Matchups[matchupKey] = mGames + 1;
                perOpponent[key] = rec;
            }
        }
        var opponents = perOpponent
            .Select(kv => new OpponentStats(
                kv.Key,
                kv.Value.Games,
                kv.Value.Wins,
                kv.Value.Losses,
                kv.Value.Matchups.OrderByDescending(m => m.Value).FirstOrDefault().Key ?? "—"))
            .OrderByDescending(o => o.Games)
            .ThenByDescending(o => o.Wins)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PlayerStats(
            entries.Count,
            wins,
            losses,
            noResult,
            certified,
            races,
            matchups,
            opponents.Take(10).ToList(),
            entries.Take(200).ToList());
    }

    private static string NonEmpty(string race) =>
        string.IsNullOrWhiteSpace(race) ? "Unknown" : race;
}
