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

    /// <summary>Repositories the install form can install from, limited to
    /// the selected variant family (and its plugins).</summary>
    public ObservableCollection<RepoEntry> Repos { get; } = new();

    /// <summary>Install options for the selected repository: Latest
    /// (track the branch) plus every commit from its checkout's log. Plugin
    /// repositories install their newest published build instead.</summary>
    public ObservableCollection<VersionOption> NewVersionOptions { get; } = new();

    /// <summary>Installed builds of the selected variant, followed by that
    /// variant's installed plugins. Every row is independent; the buttons
    /// act on the selected one.</summary>
    public ObservableCollection<InstalledEntry> Installed { get; } = new();

    public IReadOnlyList<Variant> Variants { get; } = [Variant.Original, Variant.Plus, Variant.Netcode];

    /// <summary>Plus has the unarmed "solo" mode; netcode is turn-based and
    /// only has host/guest slots, and Original stages no plugin at all.</summary>
    public IReadOnlyList<NetcodeRole> Roles => Variant == Variant.Netcode
        ? [NetcodeRole.Host, NetcodeRole.Guest]
        : [NetcodeRole.Host, NetcodeRole.Guest, NetcodeRole.Solo];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanManage))]
    [NotifyPropertyChangedFor(nameof(ProgressIndeterminate))]
    private bool _busy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(RepoHint))]
    private RepoEntry? _selectedRepo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private VersionOption? _selectedSource;

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanManage))]
    private InstalledEntry? _selectedInstall;

    /// <summary>The Original / Plus / Netcode filter. It decides which
    /// repositories can be installed, which installed rows are listed, and
    /// which plugin set a play uses.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleVisible))]
    [NotifyPropertyChangedFor(nameof(UnofficialVisible))]
    [NotifyPropertyChangedFor(nameof(InstallHeading))]
    [NotifyPropertyChangedFor(nameof(InstalledHeading))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    [NotifyPropertyChangedFor(nameof(VariantHint))]
    private Variant _variant = AppRuntime.Settings.Variant;

    [ObservableProperty]
    private NetcodeRole? _role = AppRuntime.Settings.NetcodeRole ?? NetcodeRole.Solo;

    [ObservableProperty]
    private bool _showUnofficial;

    [ObservableProperty]
    private string _status = "Pick a variant, then install or play one of its builds.";

    /// <summary>Download progress as a fraction (0-1), or -1 while nothing
    /// is downloading. Status messages carry "NN%"; this feeds the bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadPercent))]
    [NotifyPropertyChangedFor(nameof(ProgressIndeterminate))]
    private double _downloadFraction = -1;

    public double DownloadPercent => DownloadFraction < 0 ? 0 : DownloadFraction * 100;

    /// <summary>An indeterminate bar while busy work (git fetches, unpacking)
    /// runs without a byte percentage.</summary>
    public bool ProgressIndeterminate => Busy && DownloadFraction < 0;

    /// <summary>True when the hidden Remastered launch is unlocked AND the
    /// private bundle exists on this machine (see RemasteredAccess).</summary>
    [ObservableProperty]
    private bool _remasteredVisible;

    /// <summary>Unlock affordance (visible until unlocked): password box,
    /// Unlock button, status. Unlocking reveals the Play Remastered button.</summary>
    [ObservableProperty]
    private string _remasteredPassword = "";

    [ObservableProperty]
    private string _remasteredStatus = "";

    [ObservableProperty]
    private bool _showRemasteredUnlock = true;

    private string? _pendingDeleteId;
    private int _loadToken;

    /// <summary>The repository last picked in each variant family, so
    /// switching the filter back restores that choice instead of jumping to
    /// the first entry.</summary>
    private readonly Dictionary<Variant, string> _repoByVariant = new();

    public bool CanInstall => !Busy && SelectedRepo is not null && SelectedSource is not null;
    public bool CanPlay => !Busy && SelectedInstall?.CanPlay == true;
    public bool CanManage => !Busy && SelectedInstall is not null;

    public bool RoleVisible => Variant != Variant.Original;
    public bool UnofficialVisible => Variant == Variant.Original;
    public bool InstalledEmpty => Installed.Count == 0;

    public string InstallHeading => Variant switch
    {
        Variant.Plus => "Install a new Plus build or plugin",
        Variant.Netcode => "Install a new Netcode build or plugin",
        _ => "Install a new Original build",
    };

    public string InstalledHeading => $"{Variant} builds and plugins ({Installed.Count})";

    public string EmptyHint => Installed.Count == 0
        ? $"Nothing installed for {Variant} yet. Install one on the right — it appears here immediately."
        : "";

    public string SelectionHint => SelectedInstall is null
        ? "Select a build to play, or a plugin to update/remove."
        : SelectedInstall.PlayHint;

    public string VariantHint => Variant switch
    {
        Variant.Original => "Vanilla game builds: Stable (Upstream release), Prerelease, and the archives.",
        Variant.Plus => "Plus-enabled builds (the mirror's plus branch) and the Plus/rollback plugins.",
        _ => "The rollback plugin staged on the Plus-enabled builds. Pick a build and a role to play.",
    };

    /// <summary>What the currently selected repository installs, shown under
    /// the repository picker.</summary>
    public string RepoHint => SelectedRepo is null
        ? ""
        : SelectedRepo.Kind == RepoKind.Plugin
            ? $"Installs {SelectedRepo.PluginFileName} into the shared variants folder " +
              "(used when a build of this variant launches)."
            : $"Clones the game build into its own folder under versions/ ({Variant} family).";

    public PlayViewModel()
    {
        _showUnofficial = _settings.ShowUnofficialRepos;
        RemasteredVisible = RemasteredAccess.IsUnlocked(_settings);
        ShowRemasteredUnlock = !RemasteredVisible;
        _repoByVariant[Variant] = _settings.SelectedRepoId;
        RebuildRepos();
        SelectedRepo = Repos.FirstOrDefault(r => r.Id == _settings.SelectedRepoId) ?? Repos.FirstOrDefault();
        ReloadInstalled();
        LoadSources(announce: false);
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
        // Netcode has no solo mode; leaving "Solo" selected would arm slot 0.
        if (value == Variant.Netcode && Role == NetcodeRole.Solo)
            Role = NetcodeRole.Host;
        OnPropertyChanged(nameof(Roles));
        RebuildRepos();
        ReloadInstalled();
        LoadSources(announce: false);
        OnPropertyChanged(nameof(CanInstall));
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
            _repoByVariant[Variant] = value.Id;
            _settings.Save();
        }
        LoadSources(announce: false);
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnSelectedSourceChanged(VersionOption? value)
    {
        if (SelectedRepo is not null && value is not null)
            NewName = VersionManager.DefaultName(SelectedRepo, value.Sha, Variant);
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnSelectedInstallChanged(InstalledEntry? value)
    {
        _pendingDeleteId = null;
        if (value?.Version is { } version)
            _versions.Select(version.Id);
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(SelectionHint));
    }

    private void RebuildRepos()
    {
        var selectedId = _repoByVariant.TryGetValue(Variant, out var remembered)
            ? remembered
            : SelectedRepo?.Id ?? _settings.SelectedRepoId;
        Repos.Clear();
        foreach (var repo in RepoCatalog.ForVariant(Variant, ShowUnofficial))
            Repos.Add(repo);
        SelectedRepo = Repos.FirstOrDefault(r => r.Id == selectedId) ?? Repos.FirstOrDefault();
        OnPropertyChanged(nameof(RepoHint));
    }

    /// <summary>Rebuild the installed list from the selected variant's
    /// records. <paramref name="preferId"/> selects a specific row after an
    /// install; otherwise the persisted selection wins.</summary>
    private void ReloadInstalled(string? preferId = null)
    {
        var selectedId = preferId
                         ?? SelectedInstall?.Id
                         ?? (_versions.Selected is { } selected ? $"v:{selected.Id}" : null);
        Installed.Clear();
        foreach (var version in _versions.VersionsFor(Variant))
            Installed.Add(new InstalledEntry(version));
        foreach (var plugin in _versions.PluginsFor(Variant))
            Installed.Add(new InstalledEntry(plugin));
        SelectedInstall = Installed.FirstOrDefault(e => e.Id == selectedId) ?? Installed.FirstOrDefault();
        OnPropertyChanged(nameof(InstalledHeading));
        OnPropertyChanged(nameof(InstalledEmpty));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(SelectionHint));
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
                Status = $"Found {adopted} existing installation(s).";
        }
        ReloadInstalled();
        LoadSources(announce: false);
    }

    /// <summary>Hidden Play button: launches StarCraft: Remastered through
    /// the private bundle (Battle.net login handles Blizzard's own gate).</summary>
    [RelayCommand]
    private void PlayRemastered()
    {
        RemasteredAccess.Launch(_settings, out var status);
        Status = status;
    }

    /// <summary>Verify the unlock password and (re)install the private bundle.</summary>
    [RelayCommand]
    private void UnlockRemastered()
    {
        RemasteredStatus = RemasteredAccess.TryUnlock(_settings, RemasteredPassword);
        if (!RemasteredStatus.StartsWith("Wrong password", StringComparison.Ordinal))
        {
            RemasteredPassword = "";
            RemasteredVisible = RemasteredAccess.IsUnlocked(_settings);
            ShowRemasteredUnlock = !RemasteredVisible;
        }
    }

    private void LoadSources(bool announce = true)
    {
        var repo = SelectedRepo;
        _loadToken++; // drop any pending commit-log result for the old repo
        NewVersionOptions.Clear();
        if (repo is null)
        {
            SelectedSource = null;
            return;
        }

        var latest = repo.Kind == RepoKind.Plugin
            ? new VersionOption("Latest published plugin build", null)
            : new VersionOption("Latest — always update to the newest build", null);
        NewVersionOptions.Add(latest);
        SelectedSource = latest;
        NewName = VersionManager.DefaultName(repo, null, Variant);

        if (repo.Kind == RepoKind.Plugin)
        {
            if (announce)
                Status = $"{repo.Name} installs the newest plugin build into the shared variants folder.";
            return;
        }

        var dir = _versions.CanonicalDir(repo);
        if (dir is null)
        {
            if (announce)
                Status = "No checkout of this repository yet — install it first (Latest).";
            return;
        }

        var token = ++_loadToken;
        Busy = true;
        if (announce)
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
                    if (announce)
                        Status = $"{commits.Count} version(s) on {repo.Branch} — pick one, or use Latest.";
                });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (token == _loadToken)
                    {
                        DiagnosticsLog.Error($"version log: {repo.Id}", ex);
                        Status = $"Could not read versions: {ex.Message}";
                    }
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

    /// <summary>Install the chosen repository as a NEW record: a game build
    /// gets its own version folder, a plugin gets staged into variants/.
    /// Existing checkouts are never touched.</summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        var repo = SelectedRepo;
        if (repo is null || SelectedSource is null)
            return;

        var variant = Variant;
        var name = string.IsNullOrWhiteSpace(NewName) ? null : NewName.Trim();
        var sha = SelectedSource.Sha;
        PluginVersion? installedPlugin = null;
        GameVersion? installedVersion = null;
        // Re-installing a plugin updates its record instead of adding a
        // second row with the same files.
        var existingPlugin = repo.Kind == RepoKind.Plugin
            ? _settings.Plugins.FirstOrDefault(p => p.RepoId == repo.Id)
            : null;

        var ok = await RunAsync("install", async (progress, ct) =>
        {
            if (repo.Kind == RepoKind.Plugin)
                installedPlugin = await new PluginInstallService(_settings, repo)
                    .InstallAsync(existingPlugin, progress, ct);
            else
                installedVersion = await _versions.InstallAsync(repo, sha, name, progress, ct: ct,
                    variant: variant);
        });

        // Select the row the action was about: the new install, the updated
        // plugin, or (after a failure) the record VersionManager kept.
        var prefer = installedVersion is not null ? $"v:{installedVersion.Id}"
            : installedPlugin is not null ? $"p:{installedPlugin.Id}"
            : !ok && _versions.Selected is { } kept ? $"v:{kept.Id}"
            : null;
        ReloadInstalled(prefer);
        LoadSources(announce: false);
        if (ok)
            Status = installedPlugin is not null
                ? $"Installed {installedPlugin.Name}."
                : $"Installed {SelectedInstall?.Name ?? "the new version"}.";
    }

    /// <summary>Play the selected installed build. Tracked builds update to
    /// the newest build first; the version's own variant stages the plugins.</summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
        if (SelectedInstall?.Version is not { } version)
            return;
        if (Variant != Variant.Original)
        {
            _settings.NetcodeRole = Role; // persist the effective role
            _settings.Save();
        }
        await RunAsync("play", (progress, ct) => _versions.PlayAsync(version, progress, ct: ct));
        ReloadInstalled();
    }

    /// <summary>Update the selected row: a tracked build fetches the newest
    /// branch tip, a pinned build re-resets to its pin, a plugin re-downloads
    /// the newest published artifact.</summary>
    [RelayCommand]
    private async Task UpdateAsync()
    {
        var entry = SelectedInstall;
        if (entry is null)
            return;

        if (entry.Version is { } version)
        {
            await RunAsync("update", (progress, ct) => _versions.UpdateAsync(version, progress, ct: ct));
        }
        else if (entry.Plugin is { } plugin && plugin.Repo is { } repo)
        {
            await RunAsync("plugin update",
                (progress, ct) => new PluginInstallService(_settings, repo).InstallAsync(plugin, progress, ct));
        }
        ReloadInstalled(entry.Id);
    }

    /// <summary>Remove the selected row. Two clicks: the first arms, the
    /// second confirms, so a multi-gigabyte download is not lost by accident.</summary>
    [RelayCommand]
    private void Delete()
    {
        var entry = SelectedInstall;
        if (entry is null)
            return;

        if (_pendingDeleteId != entry.Id)
        {
            _pendingDeleteId = entry.Id;
            Status = $"Click Delete again to remove {entry.Name}.";
            return;
        }

        _pendingDeleteId = null;
        var name = entry.Name;
        try
        {
            if (entry.Version is { } version)
                _versions.Remove(version);
            else if (entry.Plugin is { } plugin)
                PluginInstallService.Remove(_settings, plugin);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("delete", ex);
            Status = $"Could not remove {name}: {ex.Message}";
            return;
        }

        ReloadInstalled();
        Status = $"Removed {name}.";
    }

    [RelayCommand]
    private async Task UnshallowAsync()
    {
        var repo = SelectedRepo;
        if (repo is null || repo.Kind == RepoKind.Plugin)
        {
            Status = "Pick a game repository first.";
            return;
        }
        var dir = _versions.CanonicalDir(repo);
        if (dir is null)
        {
            Status = "Install this repository first.";
            return;
        }
        await RunAsync("full log", async (progress, ct) =>
        {
            using var manager = new RepoManager(dir, repo.Url);
            await Task.Run(() => manager.Unshallow(progress), ct);
        });
        LoadSources(announce: false);
    }

    private async Task<bool> RunAsync(
        string context, Func<IProgress<string>, CancellationToken, Task> action)
    {
        Busy = true;
        Status = "Working…";
        DownloadFraction = -1;
        var progress = new Progress<string>(s =>
        {
            Status = s;
            DownloadFraction = HttpDownload.PercentOf(s) is { } percent ? percent / 100 : -1;
        });
        try
        {
            await Task.Run(() => action(progress, CancellationToken.None));
            return true;
        }
        catch (Exception ex)
        {
            // The record stays listed (marked "not downloaded yet" for a
            // build), so a failure is retryable instead of vanishing.
            DiagnosticsLog.Error(context, ex);
            Status = $"Failed: {ex.Message} (details in {DiagnosticsLog.FilePath})";
            return false;
        }
        finally
        {
            Busy = false;
            DownloadFraction = -1;
        }
    }
}
