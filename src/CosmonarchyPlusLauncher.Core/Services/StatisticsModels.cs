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

/// <summary>How often the claimed name played one race, and won with it.</summary>
public sealed record RaceStats(string Race, int Games, int Wins, int Losses)
{
    public string WinRateLabel => WinRate(Games, Wins);
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
/// the matchup the two names played most often.</summary>
public sealed record OpponentStats(
    string Name, int Games, int Wins, int Losses, string TopMatchup)
{
    public string GamesLabel => Games.ToString();
    public string WinsLabel => Wins.ToString();
    public string LossesLabel => Losses.ToString();
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
    IReadOnlyList<PlayerMatchEntry> RecentMatches)
{
    public static PlayerStats Empty => new(0, 0, 0, 0, 0, [], [], [], []);

    public string WinRateLabel => RaceStats.WinRate(Wins + Losses, Wins);
}
