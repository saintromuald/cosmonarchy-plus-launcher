using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

public partial class PlayViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppRuntime.Settings;
    private readonly VersionManager _versions = AppRuntime.Versions;

    /// <summary>Repositories the install form can install from.</summary>
    public ObservableCollection<RepoEntry> Repos { get; } = new();

    /// <summary>Install options for the selected repository: Latest
    /// (track the branch) plus every commit from its checkout's log.</summary>
    public ObservableCollection<VersionOption> NewVersionOptions { get; } = new();

    /// <summary>Installed versions. Every row is an independent checkout;
    /// the buttons act on the selected one.</summary>
    public ObservableCollection<GameVersion> Installed { get; } = new();

    public IReadOnlyList<Variant> Variants { get; } = [Variant.Original, Variant.Plus, Variant.Netcode];

    public IReadOnlyList<NetcodeRole> Roles { get; } = [NetcodeRole.Host, NetcodeRole.Guest, NetcodeRole.Solo];

    [ObservableProperty]
    private RepoEntry? _selectedRepo;

    [ObservableProperty]
    private VersionOption? _selectedSource;

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private GameVersion? _selectedInstall;

    [ObservableProperty]
    private Variant _variant = AppRuntime.Settings.Variant;

    [ObservableProperty]
    private NetcodeRole? _role = AppRuntime.Settings.NetcodeRole ?? NetcodeRole.Solo;

    [ObservableProperty]
    private bool _showUnofficial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    private bool _busy;

    [ObservableProperty]
    private string _status = "Select a repository and version, then install or play.";

    /// <summary>True when the hidden Remastered launch is unlocked AND the
    /// private bundle exists on this machine (see RemasteredAccess).</summary>
    [ObservableProperty]
    private bool _remasteredVisible;

    private string? _pendingDeleteId;
    private int _loadToken;

    public bool CanInstall => !Busy && SelectedRepo is not null && SelectedSource is not null;
    public bool CanPlay => !Busy && SelectedInstall is not null;

    public PlayViewModel()
    {
        _showUnofficial = _settings.ShowUnofficialRepos;
        RebuildRepos();
        SelectedRepo = RepoCatalog.Find(_settings.SelectedRepoId) ?? Repos.FirstOrDefault();
        ReloadInstalled();
        LoadSources();
    }

    partial void OnShowUnofficialChanged(bool value)
    {
        _settings.ShowUnofficialRepos = value;
        _settings.Save();
        RebuildRepos();
    }

    partial void OnVariantChanged(Variant value)
    {
        _settings.Variant = value;
        _settings.Save();
    }

    partial void OnRoleChanged(NetcodeRole? value)
    {
        _settings.NetcodeRole = value;
        _settings.Save();
    }

    partial void OnSelectedRepoChanged(RepoEntry? value)
    {
        if (value is not null)
        {
            _settings.SelectedRepoId = value.Id;
            _settings.Save();
        }
        LoadSources();
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnSelectedSourceChanged(VersionOption? value)
    {
        if (SelectedRepo is not null && value is not null)
            NewName = VersionManager.DefaultName(SelectedRepo, value.Sha);
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnSelectedInstallChanged(GameVersion? value)
    {
        _pendingDeleteId = null;
        if (value is not null)
            _versions.Select(value.Id);
        OnPropertyChanged(nameof(CanPlay));
    }

    private void RebuildRepos()
    {
        var selectedId = SelectedRepo?.Id ?? _settings.SelectedRepoId;
        Repos.Clear();
        foreach (var repo in VisibleRepos(ShowUnofficial))
            Repos.Add(repo);
        SelectedRepo = Repos.FirstOrDefault(r => r.Id == selectedId) ?? Repos.FirstOrDefault();
    }

    private void ReloadInstalled()
    {
        var selectedId = SelectedInstall?.Id ?? _versions.Selected?.Id;
        Installed.Clear();
        foreach (var version in _versions.Versions)
            Installed.Add(version);
        SelectedInstall = Installed.FirstOrDefault(v => v.Id == selectedId) ?? Installed.FirstOrDefault();
    }

    [RelayCommand]
    private void Refresh()
    {
        RemasteredVisible = RemasteredAccess.IsUnlocked(_settings);
        // Re-scan in case the install root holds checkouts the launcher has
        // not recorded yet (only happens while the list is empty).
        if (_versions.Versions.Count == 0)
        {
            var adopted = _versions.AdoptExistingInstallations();
            if (adopted > 0)
            {
                ReloadInstalled();
                Status = $"Found {adopted} existing installation(s).";
            }
        }
        LoadSources();
    }

    /// <summary>Hidden Play button: launches StarCraft: Remastered through
    /// the private bundle (Battle.net login handles Blizzard's own gate).</summary>
    [RelayCommand]
    private void PlayRemastered()
    {
        RemasteredAccess.Launch(_settings, out var status);
        Status = status;
    }

    private void LoadSources()
    {
        var repo = SelectedRepo;
        NewVersionOptions.Clear();
        if (repo is null)
        {
            SelectedSource = null;
            return;
        }

        var latest = new VersionOption("Latest — always update to the newest build", null);
        NewVersionOptions.Add(latest);
        SelectedSource = latest;
        NewName = VersionManager.DefaultName(repo, null);

        var dir = _versions.CanonicalDir(repo);
        if (dir is null)
        {
            Status = "No checkout of this repository yet — install it first (Latest).";
            return;
        }

        var token = ++_loadToken;
        Busy = true;
        Status = $"Reading versions of {repo.Name}…";
        _ = Task.Run(() =>
        {
            try
            {
                using var manager = new RepoManager(dir, repo.Url);
                if (_settings.FullVersionLog && manager.IsShallow())
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        Status = "Fetching the full version log (first run only)…");
                    manager.Unshallow();
                }
                var commits = manager.Log(200).Select(c => new VersionOption(
                    $"{c.Sha[..8]}  {c.Date}  {c.Message}", c.Sha)).ToList();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (token != _loadToken)
                        return; // the user moved on; drop the stale result
                    foreach (var c in commits)
                        NewVersionOptions.Add(c);
                    if (SelectedSource is null)
                        SelectedSource = latest;
                    Status = $"{commits.Count} version(s) on {repo.Branch} — pick one, or use Latest.";
                });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (token == _loadToken)
                        Status = $"Could not read versions: {ex.Message}";
                });
            }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (token == _loadToken)
                        Busy = false;
                });
            }
        });
    }

    /// <summary>Install the chosen repository + version as a NEW version:
    /// the existing checkouts are never touched.</summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        var repo = SelectedRepo;
        if (repo is null || SelectedSource is null)
            return;

        var name = string.IsNullOrWhiteSpace(NewName) ? null : NewName.Trim();
        var sha = SelectedSource.Sha;
        await RunAsync((progress, ct) =>
            _versions.InstallAsync(repo, sha, name, progress, ct: ct));
        ReloadInstalled();
        LoadSources();
    }

    /// <summary>Play the selected installed version. Tracked versions
    /// update to the newest build first; pinned versions stay put.</summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
        var version = SelectedInstall;
        if (version is null)
            return;
        _settings.NetcodeRole = Role; // persist the effective role (Solo default)
        _settings.Save();
        await RunAsync((progress, ct) => _versions.PlayAsync(version, progress, ct: ct));
        ReloadInstalled();
    }

    /// <summary>Update the selected installed version (tracked: newest
    /// build; pinned: re-reset to the pin).</summary>
    [RelayCommand]
    private async Task UpdateAsync()
    {
        var version = SelectedInstall;
        if (version is null)
            return;
        await RunAsync((progress, ct) => _versions.UpdateAsync(version, progress, ct: ct));
        ReloadInstalled();
    }

    /// <summary>Remove the selected installed version. Two clicks: the first
    /// arms, the second confirms, so a 2 GB download is not lost by accident.</summary>
    [RelayCommand]
    private void Delete()
    {
        var version = SelectedInstall;
        if (version is null)
            return;

        if (_pendingDeleteId != version.Id)
        {
            _pendingDeleteId = version.Id;
            Status = $"Click Delete again to remove {version.Name} (folder {version.Dir}).";
            return;
        }

        _pendingDeleteId = null;
        var name = version.Name;
        _versions.Remove(version);
        ReloadInstalled();
        Status = $"Removed {name}.";
    }

    [RelayCommand]
    private async Task UnshallowAsync()
    {
        var repo = SelectedRepo;
        if (repo is null)
            return;
        var dir = _versions.CanonicalDir(repo);
        if (dir is null)
        {
            Status = "Install this repository first.";
            return;
        }
        await RunAsync(async (progress, ct) =>
        {
            using var manager = new RepoManager(dir, repo.Url);
            await Task.Run(() => manager.Unshallow(progress), ct);
        });
        LoadSources();
    }

    private async Task RunAsync(Func<IProgress<string>, CancellationToken, Task> action)
    {
        Busy = true;
        Status = "Working…";
        var progress = new Progress<string>(s => Status = s);
        try
        {
            await Task.Run(() => action(progress, CancellationToken.None));
            Status = "Done.";
        }
        catch (Exception ex)
        {
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private static IEnumerable<RepoEntry> VisibleRepos(bool includeUnofficial)
    {
        foreach (var repo in RepoCatalog.Official)
            yield return repo;
        if (includeUnofficial)
            foreach (var repo in RepoCatalog.Unofficial)
                yield return repo;
    }
}
