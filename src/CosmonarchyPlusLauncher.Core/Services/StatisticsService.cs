using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Turns the ReplayKraken match list into per-player statistics:
/// races played, race-vs-race matchups, results, desyncs, opponent records,
/// and the top twenty-five most-played opponents with their top matchups.
/// Purely functional — no I/O — so the same code serves the UI today and the
/// server aggregation later.</summary>
public static class StatisticsService
{
    /// <summary>Team index ReplayKraken uses when the winner was not recorded.</summary>
    public const int UnknownWinnerTeam = 4;

    /// <summary>How many opponents make the top-opponents list.</summary>
    public const int TopOpponentCount = 25;

    /// <summary>How many race matchups are kept per opponent.</summary>
    public const int MatchupsPerOpponent = 5;

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

    /// <summary>True when the match was played on a Cosmonarchy Plus build.
    /// Matches from the self-hosted Plus server are always Plus; matches from
    /// the official list fall back to the prerelease kind (the Replays tab
    /// refines those by SHA against the fork repositories).</summary>
    internal static bool IsPlusChannel(OnlineMatch match) =>
        match.Channel == ReplayChannel.Plus ||
        ReplayBuildResolver.IsPrerelease(match.GameVersionKind);

    /// <summary>True when the named player is the only human in the match —
    /// everyone else is a computer, so the game was practice vs AI. A human
    /// teammate or opponent makes it a real game.</summary>
    public static bool IsAiOnlyMatch(OnlineMatch match, string playerName)
    {
        var others = match.Players
            .Where(p => !p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return others.All(p => p.Type.Equals("Computer", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when every player in the match is a computer (a pure
    /// AI-vs-AI sim with no humans at all). Used to filter the Replays lists,
    /// which are not scoped to one player.</summary>
    public static bool IsAllComputerMatch(OnlineMatch match) =>
        match.Players.Count > 0 &&
        match.Players.All(p => p.Type.Equals("Computer", StringComparison.OrdinalIgnoreCase));

    /// <summary>Narrow a match list to one channel (Plus / original / all).</summary>
    public static IReadOnlyList<OnlineMatch> FilterChannel(
        IEnumerable<OnlineMatch> matches, ChannelFilter channel) => channel switch
    {
        ChannelFilter.Plus => matches.Where(IsPlusChannel).ToList(),
        ChannelFilter.Original => matches.Where(m => !IsPlusChannel(m)).ToList(),
        _ => matches.ToList(),
    };

    /// <summary>Every match the claimed name played against one opponent,
    /// newest first — the head-to-head history behind the Opponents tab.
    /// Only games where both names appear are included, scoped to
    /// <paramref name="channel"/>.</summary>
    public static IReadOnlyList<PlayerMatchEntry> HeadToHead(
        IEnumerable<OnlineMatch> matches,
        string playerName,
        string opponentName,
        IReadOnlySet<string> certified,
        ChannelFilter channel = ChannelFilter.All,
        bool excludeAiGames = false)
    {
        var entries = BuildEntries(
            matches, playerName, certified, CertificationFilter.All, channel, excludeAiGames);
        return entries
            .Where(e => e.Opponents.Any(o =>
                o.Name.Equals(opponentName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Full profile for the claimed name. <paramref name="certified"/>
    /// holds the account's certified match UUIDs; the filter selects which
    /// slice of the history feeds the numbers. <paramref name="channel"/>
    /// scopes to the original game or Cosmonarchy Plus. Certification coverage
    /// is always measured against the whole history, not the slice.</summary>
    public static PlayerStats Compute(
        IEnumerable<OnlineMatch> matches,
        string playerName,
        IReadOnlySet<string> certified,
        CertificationFilter filter = CertificationFilter.All,
        ChannelFilter channel = ChannelFilter.All,
        bool excludeAiGames = false)
    {
        var allEntries = BuildEntries(
            matches, playerName, certified, CertificationFilter.All, ChannelFilter.All, excludeAiGames);
        var entries = BuildEntries(matches, playerName, certified, filter, channel, excludeAiGames);
        return Aggregate(entries, allEntries);
    }

    /// <summary>One row per match the claimed name appears in, with the
    /// account's team, race, APM, result and the human opponents faced.</summary>
    public static IReadOnlyList<PlayerMatchEntry> BuildEntries(
        IEnumerable<OnlineMatch> matches,
        string playerName,
        IReadOnlySet<string> certified,
        CertificationFilter filter = CertificationFilter.All,
        ChannelFilter channel = ChannelFilter.All,
        bool excludeAiGames = false)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return [];
        var wanted = playerName.Trim();
        var result = new List<PlayerMatchEntry>();

        foreach (var match in FilterChannel(matches, channel))
        {
            if (excludeAiGames && IsAiOnlyMatch(match, wanted))
                continue;
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
            var myApm = mine.Select(p => p.Apm).FirstOrDefault(a => a is not null);
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
                myApm,
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

    /// <summary>Aggregates the (filtered) entries into the profile and keeps
    /// <paramref name="allEntries"/> for whole-history figures such as
    /// certification coverage.</summary>
    public static PlayerStats Aggregate(
        IReadOnlyList<PlayerMatchEntry> entries, IReadOnlyList<PlayerMatchEntry>? allEntries = null)
    {
        allEntries ??= entries;
        if (entries.Count == 0)
            return PlayerStats.Empty with
            {
                TotalHistoryGames = allEntries.Count,
                TotalCertifiedGames = allEntries.Count(e => e.Certified),
            };

        var wins = entries.Count(e => e.Result == MatchResult.Win);
        var losses = entries.Count(e => e.Result == MatchResult.Loss);
        var noResult = entries.Count - wins - losses;
        var certified = entries.Count(e => e.Certified);
        var desyncs = entries.Count(e => e.Match.HasDesync);

        // Race profile: every race picked, with records and desync counts.
        // "Random" is always listed — replays usually record the resolved
        // race, so it will often be zero, but the row answers the question.
        var raceGroups = entries
            .GroupBy(e => NonEmpty(e.MyRace), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var races = raceGroups
            .Select(kv => new RaceStats(
                kv.Key,
                kv.Value.Count,
                kv.Value.Count(e => e.Result == MatchResult.Win),
                kv.Value.Count(e => e.Result == MatchResult.Loss),
                kv.Value.Count(e => e.Match.HasDesync)))
            .OrderByDescending(r => r.Games)
            .ToList();
        if (!raceGroups.ContainsKey("Random"))
            races.Add(new RaceStats("Random", 0, 0, 0, 0));

        // Race-vs-race matchups.
        var matchupGames = new Dictionary<string, (int Games, int Wins, int Losses)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var myRace = NonEmpty(entry.MyRace);
            foreach (var opponent in entry.Opponents)
            {
                var key = $"{myRace}\u0000{NonEmpty(opponent.Race)}";
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

        // Opponent records with per-opponent matchup lists.
        var perOpponent = new Dictionary<string, (int Games, int Wins, int Losses, Dictionary<string, (int Games, int Wins, int Losses)> Matchups)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var myRace = NonEmpty(entry.MyRace);
            foreach (var opponent in entry.Opponents)
            {
                var key = opponent.Name;
                perOpponent.TryGetValue(key, out var rec);
                rec.Matchups ??= new Dictionary<string, (int, int, int)>(StringComparer.OrdinalIgnoreCase);
                rec.Games++;
                if (entry.Result == MatchResult.Win)
                    rec.Wins++;
                else if (entry.Result == MatchResult.Loss)
                    rec.Losses++;
                var matchupKey = $"{myRace}\u0000{NonEmpty(opponent.Race)}";
                rec.Matchups.TryGetValue(matchupKey, out var cell);
                cell.Games++;
                if (entry.Result == MatchResult.Win)
                    cell.Wins++;
                else if (entry.Result == MatchResult.Loss)
                    cell.Losses++;
                rec.Matchups[matchupKey] = cell;
                perOpponent[key] = rec;
            }
        }
        var opponents = perOpponent
            .Select(kv => new OpponentStats(
                kv.Key,
                kv.Value.Games,
                kv.Value.Wins,
                kv.Value.Losses,
                kv.Value.Matchups
                    .Select(m => new MatchupStats(
                        m.Key.Split('\u0000')[0],
                        m.Key.Split('\u0000')[1],
                        m.Value.Games, m.Value.Wins, m.Value.Losses))
                    .OrderByDescending(m => m.Games)
                    .Take(MatchupsPerOpponent)
                    .ToList()))
            .OrderByDescending(o => o.Games)
            .ThenByDescending(o => o.Wins)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Maps.
        var maps = entries
            .GroupBy(e => NonEmpty(e.Match.MapName), StringComparer.OrdinalIgnoreCase)
            .Select(g => new MapStats(
                g.Key,
                g.Count(),
                g.Count(e => e.Result == MatchResult.Win),
                g.Count(e => e.Result == MatchResult.Loss)))
            .OrderByDescending(m => m.Games)
            .Take(8)
            .ToList();

        // APM averages (only entries that recorded one).
        double? Average(IEnumerable<PlayerMatchEntry> source) =>
            source.Select(e => e.Apm).Where(a => a is not null).Select(a => a!.Value).ToArray() is { Length: > 0 } values
                ? values.Average()
                : null;

        // Recent form: last ten results, newest first.
        var form = string.Join(" ", entries.Take(10).Select(e => e.Result switch
        {
            MatchResult.Win => "W",
            MatchResult.Loss => "L",
            _ => "–",
        }));

        var oneVsOne = entries.Count(e => e.Opponents.Count == 1);
        var plus = entries.Count(e => ReplayBuildResolver.IsPrerelease(e.Match.GameVersionKind));

        var durations = entries.Where(e => e.Match.Duration > TimeSpan.Zero).Select(e => e.Match.Duration).ToArray();
        var averageDuration = durations.Length > 0
            ? TimeSpan.FromSeconds(durations.Average(d => d.TotalSeconds))
            : (TimeSpan?)null;

        return new PlayerStats(
            entries.Count,
            wins,
            losses,
            noResult,
            certified,
            races,
            matchups,
            opponents.Take(TopOpponentCount).ToList(),
            entries.Take(200).ToList(),
            maps,
            Average(entries),
            Average(entries.Where(e => e.Result == MatchResult.Win)),
            Average(entries.Where(e => e.Result == MatchResult.Loss)),
            form,
            oneVsOne,
            entries.Count - oneVsOne,
            plus,
            entries.Count - plus,
            averageDuration,
            allEntries.Count,
            allEntries.Count(e => e.Certified),
            desyncs);
    }

    private static string NonEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
}
