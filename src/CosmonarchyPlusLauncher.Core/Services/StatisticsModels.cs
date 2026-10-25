using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Which matches feed the statistics: every game the claimed name
/// played, only games certified as "really me", or only the uncertified
/// remainder (the impostor check).</summary>
public enum CertificationFilter
{
    All,
    Certified,
    Uncertified,
}

public enum MatchResult
{
    Win,
    Loss,
    NoResult,
}

/// <summary>One match the claimed player appears in, reduced to the fields the
/// statistics tab renders (certification checklist and per-game result).</summary>
public sealed record PlayerMatchEntry(
    OnlineMatch Match,
    int MyTeam,
    string MyRace,
    MatchResult Result,
    bool Certified,
    int? Apm,
    IReadOnlyList<OnlinePlayer> Opponents)
{
    public string DateLabel => Match.DateLabel;
    public string PlayersLabel => Match.PlayersLabel;
    public string MapName => Match.MapName;
    public string ResultLabel => Result switch
    {
        MatchResult.Win => "Win",
        MatchResult.Loss => "Loss",
        _ => "—",
    };
    public string OpponentsLabel => string.Join(", ", Opponents.Select(o => o.Name));
}

/// <summary>How often the claimed name played one race, won with it, and
/// desynced with it.</summary>
public sealed record RaceStats(string Race, int Games, int Wins, int Losses, int Desyncs = 0)
{
    public string WinRateLabel => WinRate(Games, Wins);
    public string DesyncsLabel => Desyncs == 0 ? "" : $"{Desyncs} ⚠";
    internal static string WinRate(int games, int wins) =>
        games == 0 ? "—" : $"{100.0 * wins / games:0.#}%";
}

/// <summary>One cell of the race-vs-race matchup matrix (my race against one
/// opponent race). Team games contribute one row per opponent race.</summary>
public sealed record MatchupStats(string MyRace, string OpponentRace, int Games, int Wins, int Losses)
{
    public string Label => $"{MyRace} vs {OpponentRace}";
    public string WinRateLabel => RaceStats.WinRate(Games, Wins);
}

/// <summary>Aggregate against one opponent: how many games, who won them, and
/// the top race matchups the two names played.</summary>
public sealed record OpponentStats(
    string Name, int Games, int Wins, int Losses, IReadOnlyList<MatchupStats> Matchups)
{
    public string GamesLabel => Games.ToString();
    public string WinsLabel => Wins.ToString();
    public string LossesLabel => Losses.ToString();
    public string WinRateLabel => RaceStats.WinRate(Games, Wins);

    /// <summary>"Zerg vs Terran 12 (58%) · Protoss vs Zerg 5 (40%)" for the
    /// top five matchups against this opponent.</summary>
    public string MatchupSummary => string.Join("  ·  ", Matchups
        .Select(m => $"{m.Label} {m.Games} ({m.WinRateLabel})"));
}

/// <summary>How often a map was played, and the record on it.</summary>
public sealed record MapStats(string Map, int Games, int Wins, int Losses)
{
    public string GamesLabel => Games.ToString();
    public string WinRateLabel => RaceStats.WinRate(Games, Wins);
}

/// <summary>The full computed profile for one claimed player name.</summary>
public sealed record PlayerStats(
    int TotalGames,
    int Wins,
    int Losses,
    int NoResult,
    int CertifiedGames,
    IReadOnlyList<RaceStats> Races,
    IReadOnlyList<MatchupStats> Matchups,
    IReadOnlyList<OpponentStats> TopOpponents,
    IReadOnlyList<PlayerMatchEntry> RecentMatches,
    IReadOnlyList<MapStats> TopMaps,
    double? AverageApm,
    double? WinApm,
    double? LossApm,
    string FormLabel,
    int OneVsOneGames,
    int TeamGames,
    int PlusGames,
    int OriginalGames,
    TimeSpan? AverageDuration,
    int TotalHistoryGames,
    int TotalCertifiedGames,
    int DesyncGames)
{
    public static PlayerStats Empty => new(0, 0, 0, 0, 0, [], [], [], [], [], null, null, null, "", 0, 0, 0, 0, null, 0, 0, 0);

    public string WinRateLabel => RaceStats.WinRate(Wins + Losses, Wins);

    public string ApmLabel => AverageApm is { } apm
        ? $"{apm:0} avg" +
          (WinApm is { } w && LossApm is { } l ? $"  (wins {w:0} · losses {l:0})" : "")
        : "—";

    public string TeamSizeLabel => $"{OneVsOneGames} 1v1 · {TeamGames} team";

    public string ChannelLabel => $"{PlusGames} Plus · {OriginalGames} OG/other";

    public string AverageDurationLabel => AverageDuration is { } d
        ? d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}"
            : $"{d.Minutes}:{d.Seconds:00}"
        : "—";

    public string CertificationLabel => TotalHistoryGames == 0
        ? "—"
        : $"{TotalCertifiedGames} of {TotalHistoryGames} games certified";

    public string DesyncLabel => $"{DesyncGames} desynced";
}
