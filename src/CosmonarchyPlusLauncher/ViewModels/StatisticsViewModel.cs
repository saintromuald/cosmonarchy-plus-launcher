using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>One row of the certification checklist. The checkbox writes
/// straight back to the account backend.</summary>
public partial class MatchCertRow : ObservableObject
{
    private readonly Func<Task> _onChanged;

    public MatchCertRow(PlayerMatchEntry entry, Func<Task> onChanged)
    {
        Entry = entry;
        _onChanged = onChanged;
        _isCertified = entry.Certified;
    }

    public PlayerMatchEntry Entry { get; }

    public string DateLabel => Entry.DateLabel;
    public string OpponentsLabel => Entry.OpponentsLabel;
    public string RaceLabel => Entry.MyRace;
    public string ResultLabel => Entry.ResultLabel;

    [ObservableProperty]
    private bool _isCertified;

    partial void OnIsCertifiedChanged(bool value) => _ = _onChanged();
}

/// <summary>Display row for one race's stats.</summary>
public sealed record RaceRow(RaceStats Stats)
{
    public string Race => Stats.Race;
    public string Games => Stats.Games.ToString();
    public string Wins => Stats.Wins.ToString();
    public string WinRate => Stats.WinRateLabel;
}

/// <summary>Display row for one race-vs-race matchup cell.</summary>
public sealed record MatchupRow(MatchupStats Stats)
{
    public string Label => Stats.Label;
    public string Games => Stats.Games.ToString();
    public string Wins => Stats.Wins.ToString();
    public string WinRate => Stats.WinRateLabel;
}

/// <summary>Display row for one opponent in the top ten.</summary>
public sealed record OpponentRow(OpponentStats Stats, int Rank)
{
    public string RankLabel => Rank.ToString();
    public string Name => Stats.Name;
    public string Games => Stats.Games.ToString();
    public string Wins => Stats.Wins.ToString();
    public string Losses => Stats.Losses.ToString();
    public string WinRate => Stats.WinRateLabel;
    public string TopMatchup => Stats.TopMatchup;
}

public partial class StatisticsViewModel : ViewModelBase
{
    private readonly LocalAccountBackend _backend = (LocalAccountBackend)AppRuntime.AccountBackend;
    private readonly ReplayKrakenService _kraken = new();
    private IReadOnlyList<OnlineMatch> _matches = [];

    public StatisticsViewModel()
    {
        AppRuntime.LocalAccountChanged += OnAccountChanged;
        AppRuntime.CertificationsChanged += OnCertificationsChanged;
        UpdateAccountState();
    }

    // ---- Account (login / register) ---------------------------------------

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string _playerName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    public bool IsLoggedIn => AppRuntime.CurrentLocalAccount is not null;

    public string AccountLabel => AppRuntime.CurrentLocalAccount is { } a
        ? $"{a.Email} · claims “{a.PlayerName}” · created {a.CreatedLabel}"
        : "";

    [RelayCommand]
    private async Task LoginAsync()
    {
        Busy = true;
        Status = "";
        try
        {
            var session = await _backend.LoginAsync(Email.Trim(), Password);
            if (session is null)
            {
                Status = "Wrong email or password.";
                return;
            }
            Password = "";
            AppRuntime.SetLocalAccount(session.Account);
            Status = "Signed in.";
        }
        catch (Exception ex)
        {
            Status = $"Login failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        Busy = true;
        Status = "";
        try
        {
            if (Password != ConfirmPassword)
            {
                Status = "The passwords do not match.";
                return;
            }
            var (session, error) = await _backend.RegisterAsync(Email.Trim(), Password, PlayerName.Trim());
            if (session is null)
            {
                Status = error;
                return;
            }
            Password = "";
            ConfirmPassword = "";
            AppRuntime.SetLocalAccount(session.Account);
            Status = "Account created.";
        }
        catch (Exception ex)
        {
            Status = $"Could not create the account: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Logout()
    {
        AppRuntime.SetLocalAccount(null);
        Email = "";
        PlayerName = "";
        ClearStats();
        Status = "Signed out.";
    }

    [RelayCommand]
    private async Task UpdatePlayerNameAsync()
    {
        var account = AppRuntime.CurrentLocalAccount;
        if (account is null || string.IsNullOrWhiteSpace(PlayerName))
            return;
        await _backend.UpdatePlayerNameAsync(new AccountSession(account, ""), PlayerName.Trim());
        OnPropertyChanged(nameof(AccountLabel));
        AppRuntime.NotifyCertificationsChanged();
        await SyncAsync();
    }

    private void OnAccountChanged()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(AccountLabel));
        UpdateAccountState();
    }

    private void OnCertificationsChanged() => Rebuild();

    private void UpdateAccountState()
    {
        if (AppRuntime.CurrentLocalAccount is { } account)
        {
            PlayerName = account.PlayerName;
            _ = SyncAsync();
        }
    }

    // ---- Stats -------------------------------------------------------------

    public static IReadOnlyList<CertificationFilter> Filters { get; } =
        [CertificationFilter.All, CertificationFilter.Certified, CertificationFilter.Uncertified];

    [ObservableProperty] private CertificationFilter _selectedFilter = CertificationFilter.All;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _syncBusy;

    public ObservableCollection<RaceRow> RaceRows { get; } = [];
    public ObservableCollection<MatchupRow> MatchupRows { get; } = [];
    public ObservableCollection<OpponentRow> TopOpponents { get; } = [];
    public ObservableCollection<MatchCertRow> CertRows { get; } = [];

    [ObservableProperty] private string _summaryLabel = "Sign in to see your statistics.";
    [ObservableProperty] private string _winRateLabel = "—";
    [ObservableProperty] private int _totalGames;
    [ObservableProperty] private int _wins;
    [ObservableProperty] private int _losses;
    [ObservableProperty] private int _noResult;
    [ObservableProperty] private int _certifiedGames;
    [ObservableProperty] private string _syncLabel = "Statistics are computed from cosmonarchy.com (ReplayKraken) matches that include your claimed name.";

    partial void OnSelectedFilterChanged(CertificationFilter value) => Rebuild();
    partial void OnSearchChanged(string value) => RebuildCertList();

    [RelayCommand]
    private async Task SyncAsync()
    {
        var account = AppRuntime.CurrentLocalAccount;
        if (account is null)
            return;
        SyncBusy = true;
        try
        {
            var progress = new Progress<string>(s => SyncLabel = s);
            _matches = await _kraken.GetMatchesAsync(force: false, progress, CancellationToken.None);
            SyncLabel = $"{_matches.Count} matches on ReplayKraken — last synced {DateTime.Now:HH:mm}.";
            Rebuild();
        }
        catch (Exception ex)
        {
            SyncLabel = $"Could not sync: {ex.Message}";
        }
        finally
        {
            SyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        var account = AppRuntime.CurrentLocalAccount;
        if (account is null)
            return;
        SyncBusy = true;
        try
        {
            var progress = new Progress<string>(s => SyncLabel = s);
            _matches = await _kraken.GetMatchesAsync(force: true, progress, CancellationToken.None);
            SyncLabel = $"{_matches.Count} matches on ReplayKraken — refreshed {DateTime.Now:HH:mm}.";
            Rebuild();
        }
        catch (Exception ex)
        {
            SyncLabel = $"Could not sync: {ex.Message}";
        }
        finally
        {
            SyncBusy = false;
        }
    }

    /// <summary>Certify or un-certify one match as genuinely the account's.</summary>
    private async Task SetCertificationAsync(MatchCertRow row)
    {
        var account = AppRuntime.CurrentLocalAccount;
        if (account is null)
            return;
        await _backend.SetCertificationAsync(
            new AccountSession(account, ""), row.Entry.Match.MatchUuid, row.IsCertified);
        AppRuntime.NotifyCertificationsChanged();
    }

    private void Rebuild()
    {
        var account = AppRuntime.CurrentLocalAccount;
        if (account is null)
        {
            ClearStats();
            return;
        }

        var certified = LocalAccountBackend.CertifiedSet(account);
        var stats = StatisticsService.Compute(_matches, account.PlayerName, certified, SelectedFilter);

        TotalGames = stats.TotalGames;
        Wins = stats.Wins;
        Losses = stats.Losses;
        NoResult = stats.NoResult;
        CertifiedGames = stats.CertifiedGames;
        WinRateLabel = stats.WinRateLabel;
        SummaryLabel = $"Statistics for “{account.PlayerName}” — {stats.TotalGames} game(s) shown.";

        RaceRows.Clear();
        foreach (var race in stats.Races)
            RaceRows.Add(new RaceRow(race));

        MatchupRows.Clear();
        foreach (var matchup in stats.Matchups)
            MatchupRows.Add(new MatchupRow(matchup));

        TopOpponents.Clear();
        var rank = 1;
        foreach (var opponent in stats.TopOpponents)
            TopOpponents.Add(new OpponentRow(opponent, rank++));

        // The certification checklist always shows the whole history so games
        // can be certified regardless of the stats filter.
        RebuildCertList();
    }

    private void RebuildCertList(IReadOnlyList<PlayerMatchEntry>? recent = null)
    {
        var account = AppRuntime.CurrentLocalAccount;
        if (account is null)
        {
            CertRows.Clear();
            return;
        }

        var certified = LocalAccountBackend.CertifiedSet(account);
        var entries = recent ?? StatisticsService
            .BuildEntries(_matches, account.PlayerName, certified, CertificationFilter.All);

        var wanted = Search.Trim();
        if (wanted.Length > 0)
        {
            entries = entries.Where(e =>
                    e.OpponentsLabel.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                    e.MapName.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                    e.MyRace.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        CertRows.Clear();
        foreach (var entry in entries.Take(200))
        {
            MatchCertRow? row = null;
            row = new MatchCertRow(entry, () => SetCertificationAsync(row!));
            CertRows.Add(row);
        }
    }

    private void ClearStats()
    {
        TotalGames = Wins = Losses = NoResult = CertifiedGames = 0;
        WinRateLabel = "—";
        SummaryLabel = "Sign in to see your statistics.";
        RaceRows.Clear();
        MatchupRows.Clear();
        TopOpponents.Clear();
        CertRows.Clear();
        _matches = [];
    }
}
