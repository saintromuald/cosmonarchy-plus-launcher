using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>A Remastered bundle channel as shown in the Play page picker.</summary>
public sealed record RemasteredChannelOption(string Id, string Label);

/// <summary>A variant family as shown in the Play page picker: the stored
/// enum plus its product name (Original CM / Plus / Original CM Netcode).</summary>
public sealed record VariantOption(Variant Value, string Label)
{
    public override string ToString() => Label;
}

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

    /// <summary>The variant families with their product names. The stored
    /// <see cref="Variant"/> enum keeps ids stable; the picker shows labels.</summary>
    public IReadOnlyList<VariantOption> Variants { get; } =
    [
        new(Variant.Original, "Original CM"),
        new(Variant.Plus, "Plus"),
        new(Variant.Netcode, "Original CM Netcode"),
    ];

    /// <summary>The variant picker's selection; round-trips with
    /// <see cref="Variant"/> (records compare by value, so re-selecting the
    /// same option never loops).</summary>
    [ObservableProperty]
    private VariantOption? _selectedVariant;

    /// <summary>Plus has the unarmed "solo" mode first (it is the mode that
    /// works for normal play); netcode is turn-based and only has host/guest
    /// slots, and Original stages no plugin at all.</summary>
    public IReadOnlyList<NetcodeRole> Roles => Variant == Variant.Netcode
        ? [NetcodeRole.Host, NetcodeRole.Guest]
        : [NetcodeRole.Solo, NetcodeRole.Host, NetcodeRole.Guest];

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
    [NotifyPropertyChangedFor(nameof(RoleWarning))]
    [NotifyPropertyChangedFor(nameof(RoleWarningVisible))]
    private Variant _variant = AppRuntime.Settings.Variant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleWarning))]
    [NotifyPropertyChangedFor(nameof(RoleWarningVisible))]
    private NetcodeRole? _role;

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

    /// <summary>Remastered bundle channels: stable release vs. prerelease
    /// testing. Changing the selection persists it and re-checks what is
    /// installed against that channel's newest bundle.</summary>
    public IReadOnlyList<RemasteredChannelOption> RemasteredChannels { get; } =
        [new(RemasteredAccess.ChannelRelease, "Release (stable)"),
         new(RemasteredAccess.ChannelPrerelease, "Prerelease (testing)")];

    [ObservableProperty]
    private RemasteredChannelOption? _remasteredChannel;

    /// <summary>One-line summary distinguishing what is installed from what
    /// the selected channel offers: installed tag + channel, and whether the
    /// selected channel is up to date, newer, or not installed.</summary>
    [ObservableProperty]
    private string _remasteredVersionStatus = "";

    /// <summary>True when the installed bundle does not match the selected
    /// channel's newest bundle (or nothing is installed): the Download
    /// affordance is shown and the password row can be revealed.</summary>
    [ObservableProperty]
    private bool _remasteredDownloadVisible;

    /// <summary>Unlock affordance: password box, Unlock/Download button,
    /// status. Visible until unlocked, and again whenever a channel download
    /// is pending. Unlocking reveals the Remastered play controls.</summary>
    [ObservableProperty]
    private string _remasteredPassword = "";

    [ObservableProperty]
    private string _remasteredStatus = "";

    [ObservableProperty]
    private bool _showRemasteredUnlock = true;

    /// <summary>True while the password row is being used to fetch the
    /// selected channel's bundle (not a first-time unlock).</summary>
    private bool _remasteredDownloadPending;

    private int _remasteredToken;

    private string? _pendingDeleteId;
    private int _loadToken;
    private bool _suppressRoleSave;

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
        Variant.Netcode => "Install a new Original CM Netcode build or plugin",
        _ => "Install a new Original CM build",
    };

    public string InstalledHeading => $"{Variant.Label()} builds and plugins ({Installed.Count})";

    public string EmptyHint => Installed.Count == 0
        ? $"Nothing installed for {Variant.Label()} yet. Install one on the right — it appears here immediately."
        : "";

    public string SelectionHint => SelectedInstall is null
        ? "Select a build to play, or a plugin to update/remove."
        : SelectedInstall.PlayHint;

    public string VariantHint => Variant switch
    {
        Variant.Original => "Vanilla Cosmonarchy (Original CM): the upstream Stable and Prerelease channels and the old archives. No plugins.",
        Variant.Plus => "Cosmonarchy Plus: the full product — Plus gameplay with online capability. Plus Netcode applies only when you host or join; Solo plays offline.",
        _ => "Original CM Netcode: vanilla builds with the Plus Netcode plugin armed. Pick a build, then host or join a match.",
    };

    /// <summary>Warn when the chosen role applies the Plus Netcode turn
    /// system: it takes over mouse and keyboard input while it is under
    /// development, so normal play must use Solo (Plus) or avoid launching
    /// Original CM Netcode builds.</summary>
    public string RoleWarning => Variant == Variant.Plus && Role is not null and not NetcodeRole.Solo
        ? "Host/Guest applies the rollback netcode: both players need the same build and the launcher-installed CrownLink (with the rollback channel). Experimental — if the match falls back to lockstep, check that both sides armed it."
        : Variant == Variant.Netcode
            ? "Original CM Netcode runs armed — both players must run the same build and the rollback-capable CrownLink.snp. Close the game to end the session."
            : "";

    public bool RoleWarningVisible => RoleWarning.Length > 0;

    /// <summary>What the currently selected repository installs, shown under
    /// the repository picker.</summary>
    public string RepoHint => SelectedRepo is null
        ? ""
        : SelectedRepo.Kind == RepoKind.Plugin
            ? $"Not a game version — installs {SelectedRepo.PluginFileName} into the shared plugins " +
              "folder, applied automatically when a build of this variant launches."
            : $"Clones the game build into its own folder under versions/ ({Variant.Label()} family).";

    public PlayViewModel()
    {
        _showUnofficial = _settings.ShowUnofficialRepos;
        RemasteredVisible = RemasteredAccess.IsUnlocked(_settings);
        ShowRemasteredUnlock = !RemasteredVisible;
        _remasteredChannel = RemasteredChannels.FirstOrDefault(c => c.Id == _settings.RemasteredChannel)
                             ?? RemasteredChannels[0];
        RefreshRemasteredState();
        _repoByVariant[Variant] = _settings.SelectedRepoId;
        _role = Variant == Variant.Netcode
            ? _settings.NetcodeRole ?? NetcodeRole.Host
            : _settings.PlusRole;
        _selectedVariant = Variants.FirstOrDefault(v => v.Value == Variant) ?? Variants[0];
        RebuildRepos();
        SelectedRepo = Repos.FirstOrDefault(r => r.Id == _settings.SelectedRepoId) ?? Repos.FirstOrDefault();
        ReloadInstalled();
        LoadSources(announce: false);
        // The update banner checks on its own when the page loads.
        _ = CheckForLauncherUpdateAsync();
    }

    partial void OnShowUnofficialChanged(bool value)
    {
        _settings.ShowUnofficialRepos = value;
        _settings.Save();
        RebuildRepos();
    }

    partial void OnSelectedVariantChanged(VariantOption? value)
    {
        if (value is not null && value.Value != Variant)
            Variant = value.Value;
    }

    partial void OnVariantChanged(Variant value)
    {
        SelectedVariant = Variants.FirstOrDefault(v => v.Value == value);
        _settings.Variant = value;
        _settings.Save();
        // Each family keeps its own role: switching filters must not arm the
        // rollback turn system on a Plus play just because Netcode was viewed.
        _suppressRoleSave = true;
        Role = value == Variant.Netcode
            ? _settings.NetcodeRole ?? NetcodeRole.Host
            : _settings.PlusRole;
        _suppressRoleSave = false;
        OnPropertyChanged(nameof(Roles));
        OnPropertyChanged(nameof(RoleWarning));
        OnPropertyChanged(nameof(RoleWarningVisible));
        RebuildRepos();
        ReloadInstalled();
        LoadSources(announce: false);
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnRoleChanged(NetcodeRole? value)
    {
        if (_suppressRoleSave)
            return;
        if (Variant == Variant.Netcode)
            _settings.NetcodeRole = value;
        else
            _settings.PlusRole = value ?? NetcodeRole.Solo;
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
        RefreshRemasteredState();
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

    /// <summary>Reveal the password row so the selected channel's bundle can
    /// be fetched. The Unlock command re-verifies the password, then
    /// downloads and decrypts the selected channel's newest bundle when what
    /// is installed does not match it.</summary>
    [RelayCommand]
    private void PrepareRemasteredDownload()
    {
        _remasteredDownloadPending = true;
        UpdateRemasteredPrompt();
        Status = $"Enter the password to download the {SelectedChannelLabel()} bundle.";
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
            _remasteredDownloadPending = false;
            RefreshRemasteredState();
        }
    }

    partial void OnRemasteredChannelChanged(RemasteredChannelOption? value)
    {
        if (value is null)
            return;
        _settings.RemasteredChannel = value.Id;
        _settings.Save();
        _remasteredDownloadPending = false;
        RefreshRemasteredState();
    }

    /// <summary>Password-row caption: first-time unlock or a channel
    /// download both reuse the same box.</summary>
    public string RemasteredPromptLabel =>
        _remasteredDownloadPending
            ? $"Password to download the {SelectedChannelLabel()} bundle"
            : "Access password";

    /// <summary>The password-row button confirms an unlock, or downloads the
    /// selected channel's bundle when that is what the row was opened for.</summary>
    public string RemasteredUnlockButtonLabel => _remasteredDownloadPending ? "Download" : "Unlock";

    private string SelectedChannelLabel() =>
        RemasteredChannel?.Label ?? RemasteredAccess.ChannelRelease;

    private string ChannelLabel(string id) =>
        RemasteredChannels.FirstOrDefault(c => c.Id == id)?.Label ?? id;

    /// <summary>Recompute what is installed vs. the selected channel's newest
    /// bundle. The installed side is read from disk immediately; the remote
    /// tag resolves off the UI thread (network) behind a token so a stale
    /// result never overwrites a newer selection.</summary>
    private void RefreshRemasteredState()
    {
        var (tag, channel) = RemasteredAccess.ReadInstalledVersion();
        var selected = RemasteredChannel?.Id ?? RemasteredAccess.ChannelRelease;
        var token = ++_remasteredToken;
        Task.Run(() =>
        {
            string latestTag;
            try
            {
                latestTag = RemasteredAccess.ResolveLatest(selected).Tag;
            }
            catch
            {
                latestTag = "";
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (token != _remasteredToken)
                    return;
                UpdateRemasteredStatus(tag, channel, selected, latestTag);
            });
        });
    }

    private void UpdateRemasteredStatus(string tag, string? channel, string selected, string latestTag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            RemasteredVersionStatus =
                $"No Remastered bundle installed ({SelectedChannelLabel()} selected).";
            RemasteredDownloadVisible = true;
        }
        else
        {
            var installedLabel = ChannelLabel(channel ?? "unknown");
            var sameChannel = channel == selected;
            var upToDate = sameChannel && !string.IsNullOrEmpty(latestTag) && latestTag == tag;
            if (upToDate)
                RemasteredVersionStatus = $"Installed: {tag} ({installedLabel}) — up to date.";
            else if (sameChannel)
                RemasteredVersionStatus = string.IsNullOrEmpty(latestTag)
                    ? $"Installed: {tag} ({installedLabel}) — could not check for updates (offline?)."
                    : $"Installed: {tag} ({installedLabel}) — update available: {latestTag}.";
            else
                RemasteredVersionStatus = string.IsNullOrEmpty(latestTag)
                    ? $"Installed: {tag} ({installedLabel}) — select it to switch back."
                    : $"Installed: {tag} ({installedLabel}) — {SelectedChannelLabel()} has {latestTag}; download to switch.";
            RemasteredDownloadVisible = !upToDate;
        }
        UpdateRemasteredPrompt();
    }

    private void UpdateRemasteredPrompt()
    {
        ShowRemasteredUnlock = !RemasteredVisible || (_remasteredDownloadPending && RemasteredDownloadVisible);
        OnPropertyChanged(nameof(RemasteredPromptLabel));
        OnPropertyChanged(nameof(RemasteredUnlockButtonLabel));
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
                Status = $"{repo.Name} installs the newest plugin build into the shared plugins folder.";
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
        // Persist the effective role for this family so a restart keeps it.
        if (Variant == Variant.Netcode)
            _settings.NetcodeRole = Role;
        else if (Variant == Variant.Plus)
            _settings.PlusRole = Role ?? NetcodeRole.Solo;
        _settings.Save();
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
