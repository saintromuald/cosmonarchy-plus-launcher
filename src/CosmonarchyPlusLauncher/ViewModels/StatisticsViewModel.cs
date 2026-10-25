using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>One row of the certification checklist. The checkbox writes
/// straight back to the account backend; the certification record (when
/// present) carries who certified — email + in-game name.</summary>
public partial class MatchCertRow : ObservableObject
{
    private readonly Func<Task> _onChanged;

    public MatchCertRow(PlayerMatchEntry entry, Certification? certification, Func<Task> onChanged)
    {
        Entry = entry;
        Certification = certification;
        _onChanged = onChanged;
        _isCertified = entry.Certified;
    }

    public PlayerMatchEntry Entry { get; }
    public Certification? Certification { get; }

    public string DateLabel => Entry.DateLabel;
    public string OpponentsLabel => Entry.OpponentsLabel;
    public string RaceLabel => Entry.MyRace;
    public string ResultLabel => Entry.ResultLabel;
    public string CertByLabel => Certification?.IdentityLabel ?? "";

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
    public string Desyncs => Stats.DesyncsLabel;
}

/// <summary>Display row for one race-vs-race matchup cell.</summary>
public sealed record MatchupRow(MatchupStats Stats)
{
    public string Label => Stats.Label;
    public string Games => Stats.Games.ToString();
    public string Wins => Stats.Wins.ToString();
    public string WinRate => Stats.WinRateLabel;
}

/// <summary>Display row for one opponent in the top twenty-five.</summary>
public sealed record OpponentRow(OpponentStats Stats, int Rank)
{
    public string RankLabel => Rank.ToString();
    public string Name => Stats.Name;
    public string Games => Stats.Games.ToString();
    public string Wins => Stats.Wins.ToString();
    public string Losses => Stats.Losses.ToString();
    public string WinRate => Stats.WinRateLabel;
    public string MatchupSummary => Stats.MatchupSummary;
}

/// <summary>Display row for one map in the profile.</summary>
public sealed record MapRow(MapStats Stats)
{
    public string Map => Stats.Map;
    public string Games => Stats.Games.ToString();
    public string Wins => Stats.Wins.ToString();
    public string WinRate => Stats.WinRateLabel;
}

/// <summary>One game in the head-to-head history behind an opponent row:
/// the matchup, result, map, length and badges (desync, certified).</summary>
public sealed record OpponentMatchRow(PlayerMatchEntry Entry, string OpponentRace)
{
    public string DateLabel => Entry.Match.DateLabel;
    public string MyRace => Entry.MyRace;
    public string MatchupLabel => $"{ShortRace(Entry.MyRace)} vs {ShortRace(OpponentRace)}";
    public string ResultLabel => Entry.ResultLabel;
    public string MapLabel => Entry.Match.MapName;
    public string DurationLabel => Entry.Match.DurationLabel;
    public string Badges => string.Concat(
        Entry.Match.HasDesync ? "⚠ " : "",
        Entry.Certified ? "✓" : "");

    private static string ShortRace(string race) => race switch
    {
        "Terran" => "T",
        "Protoss" => "P",
        "Zerg" => "Z",
        "Askosi" => "A",
        _ => "O",
    };
}

/// <summary>One stored account offered by the switcher. Accounts on this
/// machine were authenticated when they were added, so switching between
/// them does not ask for the password again.</summary>
public sealed record AccountOption(LocalAccount Account)
{
    public string Label => $"{Account.Email} — {Account.PlayerName}";
}

/// <summary>One channel choice: everything together, the original game, or
/// Cosmonarchy Plus only.</summary>
public sealed record ChannelOption(ChannelFilter Value, string Label);

public partial class StatisticsViewModel : ViewModelBase
{
    private readonly LocalAccountBackend _backend = (LocalAccountBackend)AppRuntime.AccountBackend;
    private readonly ReplayKrakenService _kraken = new();
    private readonly ChannelFilter _fixedChannel;
    private IReadOnlyList<OnlineMatch> _matches = [];

    public StatisticsViewModel(ChannelFilter fixedChannel = ChannelFilter.All)
    {
        _fixedChannel = fixedChannel;
        _selectedChannel = Channels.First(c => c.Value == fixedChannel);
        AppRuntime.LocalAccountChanged += OnAccountChanged;
        AppRuntime.CertificationsChanged += OnCertificationsChanged;
        UpdateAccountState();
    }

    /// <summary>False on the dedicated Statistics Plus tab, where the tab
    /// itself is the channel; the selector hides.</summary>
    public bool CanSelectChannel => _fixedChannel == ChannelFilter.All;

    // ---- Account (login / register) ---------------------------------------

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string _playerName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    /// <summary>True while the login / create-account form is showing (no
    /// account active, the user logged out, or they pressed "Add account").</summary>
    [ObservableProperty] private bool _isAuthMode = true;

    /// <summary>All accounts stored on this machine — the switcher list.</summary>
    public ObservableCollection<AccountOption> Accounts { get; } = [];

    [ObservableProperty] private AccountOption? _selectedAccount;

    public bool IsLoggedIn => AppRuntime.CurrentLocalAccount is not null;

    public bool HasStoredAccounts => Accounts.Count > 0;

    /// <summary>The auth form can be dismissed when an account is still
    /// active behind it (the "Add account" flow).</summary>
    public bool CanCancelAuth => AppRuntime.CurrentLocalAccount is not null;

    public string AccountLabel => AppRuntime.CurrentLocalAccount is { } a
        ? $"{a.Email} · claims “{a.PlayerName}” · created {a.CreatedLabel}"
        : "";

    partial void OnSelectedAccountChanged(AccountOption? value)
    {
        // In the auth form the selection is a pending choice for
        // "Continue as selected"; elsewhere it switches immediately.
        if (value is null || IsAuthMode)
            return;
        if (AppRuntime.CurrentLocalAccount?.Id == value.Account.Id)
            return;
        AppRuntime.SetLocalAccount(value.Account);
    }

    [RelayCommand]
    private void AddAccount() => IsAuthMode = true;

    [RelayCommand]
    private void CancelAuth()
    {
        if (AppRuntime.CurrentLocalAccount is not null)
            IsAuthMode = false;
    }

    [RelayCommand]
    private void ContinueAs()
    {
        if (SelectedAccount is { } option)
            AppRuntime.SetLocalAccount(option.Account);
    }

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
        Email = "";
        PlayerName = "";
        Status = "Signed out. Accounts on this machine stay available in the switcher.";
        AppRuntime.SetLocalAccount(null); // persists "nobody active" + raises LocalAccountChanged
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
        OnPropertyChanged(nameof(CanCancelAuth));
        UpdateAccountState();
    }

    private void OnCertificationsChanged() => Rebuild();

    private void UpdateAccountState()
    {
        // Set the mode first: RebuildAccounts assigns the selected option,
        // and that assignment auto-switches accounts unless the auth form is
        // showing (where the selection is a pending "continue as" choice).
        if (AppRuntime.CurrentLocalAccount is { } account)
        {
            IsAuthMode = false;
            PlayerName = account.PlayerName;
        }
        else
        {
            IsAuthMode = true;
            ClearStats();
        }
        IsOpponentDetailOpen = false;
        SelectedOpponent = null;
        RebuildAccounts();
        if (AppRuntime.CurrentLocalAccount is not null)
            _ = SyncAsync();
    }

    private void RebuildAccounts()
    {
        var activeId = AppRuntime.CurrentLocalAccount?.Id;
        Accounts.Clear();
        foreach (var account in _backend.ListAccounts())
            Accounts.Add(new AccountOption(account));
        OnPropertyChanged(nameof(HasStoredAccounts));
        SelectedAccount = Accounts.FirstOrDefault(a => a.Account.Id == activeId)
                          ?? Accounts.FirstOrDefault();
    }

    // ---- Stats -------------------------------------------------------------

    public static IReadOnlyList<CertificationFilter> Filters { get; } =
        [CertificationFilter.All, CertificationFilter.Certified, CertificationFilter.Uncertified];

    /// <summary>The channel choices that split the statistics into the
    /// original game and Cosmonarchy Plus.</summary>
    public static IReadOnlyList<ChannelOption> Channels { get; } =
    [
        new(ChannelFilter.All, "All channels"),
        new(ChannelFilter.Original, "Cosmonarchy OG"),
        new(ChannelFilter.Plus, "Cosmonarchy Plus"),
    ];

    [ObservableProperty] private CertificationFilter _selectedFilter = CertificationFilter.All;
    [ObservableProperty] private ChannelOption _selectedChannel;

    /// <summary>True hides games where the claimed name is the only human —
    /// practice games against AI bots. On by default; the checkbox is next to
    /// the filters.</summary>
    [ObservableProperty] private bool _hideAiGames = true;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _syncBusy;

    public ObservableCollection<RaceRow> RaceRows { get; } = [];
    public ObservableCollection<MatchupRow> MatchupRows { get; } = [];
    public ObservableCollection<OpponentRow> TopOpponents { get; } = [];
    public ObservableCollection<MapRow> MapRows { get; } = [];
    public ObservableCollection<MatchCertRow> CertRows { get; } = [];

    [ObservableProperty] private string _summaryLabel = "Sign in to see your statistics.";
    [ObservableProperty] private string _winRateLabel = "—";
    [ObservableProperty] private int _totalGames;
    [ObservableProperty] private int _wins;
    [ObservableProperty] private int _losses;
    [ObservableProperty] private int _noResult;
    [ObservableProperty] private int _certifiedGames;
    [ObservableProperty] private int _desyncGames;
    [ObservableProperty] private string _apmLabel = "—";
    [ObservableProperty] private string _formLabel = "";
    [ObservableProperty] private string _teamSizeLabel = "—";
    [ObservableProperty] private string _channelLabel = "—";
    [ObservableProperty] private string _averageDurationLabel = "—";
    [ObservableProperty] private string _certificationLabel = "—";
    [ObservableProperty] private string _syncLabel = "Statistics are computed from cosmonarchy.com (ReplayKraken) matches that include your claimed name.";

    // ---- Opponent history (head-to-head) -----------------------------------

    [ObservableProperty] private OpponentRow? _selectedOpponent;

    /// <summary>True while the head-to-head history of one opponent is open
    /// (double-clicked); the opponents list hides behind it.</summary>
    [ObservableProperty] private bool _isOpponentDetailOpen;

    [ObservableProperty] private string _opponentDetailHeader = "";
    [ObservableProperty] private string _opponentDetailSummary = "";

    public ObservableCollection<OpponentMatchRow> OpponentMatches { get; } = [];

    /// <summary>Open every game between the active account and one opponent.
    /// Called from the double-click handler and the "View games" button.</summary>
    public void OpenOpponent(OpponentRow? row)
    {
        if (row is null || AppRuntime.CurrentLocalAccount is not { } account)
            return;
        var certified = LocalAccountBackend.CertifiedSet(account);
        var entries = StatisticsService.HeadToHead(
            _matches, account.PlayerName, row.Name, certified, SelectedChannel.Value, HideAiGames);

        OpponentMatches.Clear();
        foreach (var entry in entries)
        {
            var oppRace = entry.Opponents.FirstOrDefault(o =>
                    o.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase))?.Race
                ?? "Unknown";
            OpponentMatches.Add(new OpponentMatchRow(entry, oppRace));
        }

        OpponentDetailHeader = $"History vs {row.Name}";
        OpponentDetailSummary = $"{row.Games} games · {row.Wins} wins · {row.Losses} losses · win rate {row.WinRate}";
        IsOpponentDetailOpen = true;
    }

    [RelayCommand]
    private void ViewOpponentGames() => OpenOpponent(SelectedOpponent);

    [RelayCommand]
    private void CloseOpponent()
    {
        IsOpponentDetailOpen = false;
        SelectedOpponent = null;
    }

    partial void OnSelectedFilterChanged(CertificationFilter value) => Rebuild();
    partial void OnSelectedChannelChanged(ChannelOption? value) => Rebuild();
    partial void OnHideAiGamesChanged(bool value) => Rebuild();
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
            _matches = await FetchMatchesAsync(force: false, progress);
            SyncLabel = $"{_matches.Count} matches — last synced {DateTime.Now:HH:mm}.";
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
            _matches = await FetchMatchesAsync(force: true, progress);
            SyncLabel = $"{_matches.Count} matches — refreshed {DateTime.Now:HH:mm}.";
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

    /// <summary>The official match list plus the self-hosted Plus server's,
    /// deduplicated by match uuid (the server copy wins for pre-split games
    /// that still sit on both).</summary>
    private async Task<IReadOnlyList<OnlineMatch>> FetchMatchesAsync(bool force, IProgress<string> progress)
    {
        var byUuid = new Dictionary<string, OnlineMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in await _kraken.GetMatchesAsync(force, progress, CancellationToken.None))
            byUuid[m.MatchUuid] = m;

        var plusServer = AppRuntime.Settings.PlusReplayServer.Trim();
        if (plusServer.Length > 0)
        {
            try
            {
                var plusKraken = new ReplayKrakenService(apiBase: plusServer);
                foreach (var m in await plusKraken.GetPlusMatchesAsync(force, progress, CancellationToken.None))
                    byUuid[m.MatchUuid] = m;
            }
            catch (Exception ex)
            {
                progress.Report($"Could not load the Plus server list ({ex.Message}); using the official list.");
            }
        }

        return byUuid.Values.OrderByDescending(m => m.UploadedAt).ToList();
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
        var stats = StatisticsService.Compute(
            _matches, account.PlayerName, certified, SelectedFilter, SelectedChannel.Value, HideAiGames);

        TotalGames = stats.TotalGames;
        Wins = stats.Wins;
        Losses = stats.Losses;
        NoResult = stats.NoResult;
        CertifiedGames = stats.CertifiedGames;
        DesyncGames = stats.DesyncGames;
        WinRateLabel = stats.WinRateLabel;
        ApmLabel = stats.ApmLabel;
        FormLabel = stats.FormLabel;
        TeamSizeLabel = stats.TeamSizeLabel;
        ChannelLabel = stats.ChannelLabel;
        AverageDurationLabel = stats.AverageDurationLabel;
        CertificationLabel = stats.CertificationLabel;
        SummaryLabel = $"Statistics for “{account.PlayerName}” — {stats.TotalGames} game(s) shown" +
                       (HideAiGames ? " · AI games hidden" : "") + ".";

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

        MapRows.Clear();
        foreach (var map in stats.TopMaps)
            MapRows.Add(new MapRow(map));

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
            .BuildEntries(_matches, account.PlayerName, certified, CertificationFilter.All, SelectedChannel.Value, HideAiGames);

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
            var certification = LocalAccountBackend.CertificationFor(account, entry.Match.MatchUuid);
            MatchCertRow? row = null;
            row = new MatchCertRow(entry, certification, () => SetCertificationAsync(row!));
            CertRows.Add(row);
        }
    }

    private void ClearStats()
    {
        TotalGames = Wins = Losses = NoResult = CertifiedGames = DesyncGames = 0;
        WinRateLabel = "—";
        ApmLabel = "—";
        FormLabel = "";
        TeamSizeLabel = "—";
        ChannelLabel = "—";
        AverageDurationLabel = "—";
        CertificationLabel = "—";
        SummaryLabel = "Sign in to see your statistics.";
        RaceRows.Clear();
        MatchupRows.Clear();
        TopOpponents.Clear();
        MapRows.Clear();
        CertRows.Clear();
        _matches = [];
    }
}
