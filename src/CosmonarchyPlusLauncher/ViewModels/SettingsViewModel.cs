using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppRuntime.Settings;

    public IReadOnlyList<ActionOnPlay> ActionOnPlayOptions { get; } =
        [ActionOnPlay.StayOpen, ActionOnPlay.Minimize, ActionOnPlay.Close];

    public string CurrentVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "dev";

    [ObservableProperty]
    private string _rootPath = AppRuntime.Settings.RootPath;

    [ObservableProperty]
    private string _linuxPrefixRoot = AppRuntime.Settings.LinuxPrefixRoot;

    [ObservableProperty]
    private ActionOnPlay _actionOnPlay = AppRuntime.Settings.ActionOnPlay;

    [ObservableProperty]
    private bool _fullVersionLog = AppRuntime.Settings.FullVersionLog;

    [ObservableProperty]
    private string _discordWebhookUrl = AppRuntime.Settings.DiscordWebhookUrl ?? "";

    /// <summary>Base URL of the self-hosted Plus replay server (empty = Plus
    /// games come from the official ReplayKraken list).</summary>
    [ObservableProperty]
    private string _plusReplayServer = AppRuntime.Settings.PlusReplayServer;

    /// <summary>Unlock fields for the hidden Remastered launch.</summary>
    [ObservableProperty]
    private string _remasteredPassword = "";

    [ObservableProperty]
    private string _remasteredBundleUrl = AppRuntime.Settings.RemasteredBundleUrl ?? "";

    public IReadOnlyList<string> RemasteredChannels { get; } =
        [RemasteredAccess.ChannelRelease, RemasteredAccess.ChannelPrerelease];

    [ObservableProperty]
    private string _remasteredChannel = AppRuntime.Settings.RemasteredChannel;

    [ObservableProperty]
    private string _remasteredStatus =
        RemasteredAccess.IsUnlocked(AppRuntime.Settings)
            ? "Unlocked on this machine."
            : (RemasteredAccess.IsBundleInstalled ? "Locked." : "Locked (bundle not installed).");

    /// <summary>Installed bundle marker + channel as one label, e.g.
    /// "v1-pre1 (prerelease)" or "not installed".</summary>
    [ObservableProperty]
    private string _remasteredInstalled = FormatRemasteredInstalled();

    /// <summary>Newest bundle release tag of the selected channel: "not
    /// checked" before the first check, "…" while checking, "offline" when
    /// the check could not reach GitHub.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemasteredUpdateAvailable))]
    private string _remasteredLatest = "not checked";

    /// <summary>True when the installed bundle is the newest release of the
    /// selected channel — drives the "✓ up to date" confirmation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemasteredUpdateAvailable))]
    private bool _remasteredUpToDate;

    /// <summary>True when a newer bundle exists for the selected channel —
    /// shows the "Update to latest" button.</summary>
    public bool RemasteredUpdateAvailable =>
        !RemasteredUpToDate &&
        RemasteredLatest is not ("not checked" or "offline" or "…" or "");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemasteredUpdateStatus))]
    private string _remasteredUpdateStatus = "";

    public bool HasRemasteredUpdateStatus => RemasteredUpdateStatus.Length > 0;

    [ObservableProperty]
    private bool _remasteredChecking;

    [ObservableProperty]
    private string _communityRepo = AppRuntime.Settings.CommunityRepo;

    [ObservableProperty]
    private string _githubToken = AppRuntime.Settings.GitHubToken ?? "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _updateStatus = "";

    /// <summary>The newest released version, per the last check ("…" while
    /// checking, "not checked" before the first check, "offline" when the
    /// check could not reach GitHub).</summary>
    [ObservableProperty]
    private string _latestVersion = "not checked";

    /// <summary>The newest prerelease tag, or "" when none exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrerelease))]
    [NotifyPropertyChangedFor(nameof(PrereleaseLabel))]
    private string _latestPrerelease = "";

    public bool HasPrerelease => LatestPrerelease.Length > 0;

    public string PrereleaseLabel => $"prerelease: {LatestPrerelease}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBarIndeterminate))]
    private bool _updateBusy;

    /// <summary>Download progress as a fraction (0-1), or -1 while nothing
    /// is downloading. The update status carries "NN%"; this feeds the bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatePercent))]
    [NotifyPropertyChangedFor(nameof(UpdateBarIndeterminate))]
    private double _downloadFraction = -1;

    public double UpdatePercent => DownloadFraction < 0 ? 0 : DownloadFraction * 100;

    /// <summary>An indeterminate bar while busy work (checking, extracting)
    /// runs without a byte percentage.</summary>
    public bool UpdateBarIndeterminate => UpdateBusy && DownloadFraction < 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    private bool _hasUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    private bool _canInstallUpdate;

    /// <summary>The fallback for installs that cannot replace themselves:
    /// an update exists, but the download button is what applies.</summary>
    public bool CanDownloadUpdate => HasUpdate && !CanInstallUpdate;

    [ObservableProperty]
    private bool _canOpenRelease;

    private UpdateInfo? _update;

    public SettingsViewModel()
    {
        // Populate the version display as soon as the page opens (the result
        // lands in LatestVersion/UpdateStatus; the button re-checks on demand).
        _ = CheckForUpdatesAsync();

        // Show the remastered bundle's up-to-date state as soon as the page
        // opens for unlocked machines (the button re-checks on demand).
        if (RemasteredAccess.IsUnlocked(AppRuntime.Settings))
            _ = CheckRemasteredAsync();
    }

    [RelayCommand]
    private void UnlockRemastered()
    {
        RemasteredStatus = RemasteredAccess.TryUnlock(_settings, RemasteredPassword);
        if (!RemasteredStatus.StartsWith("Wrong password", StringComparison.Ordinal))
        {
            RemasteredPassword = "";
            RemasteredInstalled = FormatRemasteredInstalled();
            _ = CheckRemasteredAsync();
        }
    }

    /// <summary>Installed bundle marker + channel as one label.</summary>
    private static string FormatRemasteredInstalled()
    {
        var (tag, channel) = RemasteredAccess.ReadInstalledVersion();
        if (tag.Length == 0)
            return RemasteredAccess.IsBundleInstalled ? "unknown" : "not installed";
        return channel is null ? tag : $"{tag} ({channel})";
    }

    /// <summary>Check the selected bundle channel and update the visual
    /// up-to-date confirmation.</summary>
    [RelayCommand]
    private async Task CheckRemasteredAsync()
    {
        if (!string.IsNullOrWhiteSpace(RemasteredBundleUrl))
        {
            RemasteredLatest = "manual";
            RemasteredUpToDate = true;
            RemasteredUpdateStatus = "A manual bundle URL is set — channel updates are skipped.";
            return;
        }
        RemasteredChecking = true;
        RemasteredUpToDate = false;
        RemasteredLatest = "…";
        RemasteredUpdateStatus = $"Checking the {RemasteredChannel} channel…";
        try
        {
            var latest = await Task.Run(
                () => RemasteredAccess.TryResolveLatest(RemasteredChannel));
            var (installedTag, _) = RemasteredAccess.ReadInstalledVersion();
            if (latest is null)
            {
                RemasteredLatest = "offline";
                RemasteredUpdateStatus = "Could not check the bundle channel (offline or rate-limited?).";
            }
            else
            {
                RemasteredLatest = latest.Tag;
                RemasteredUpToDate = installedTag == latest.Tag;
                RemasteredUpdateStatus = RemasteredUpToDate
                    ? $"This machine is on the latest {RemasteredChannel} bundle ({latest.Tag})."
                    : $"A newer {RemasteredChannel} bundle is available ({latest.Tag}). Enter the unlock password and press “Update to latest”.";
            }
        }
        catch (Exception ex)
        {
            RemasteredLatest = "offline";
            RemasteredUpdateStatus = $"Could not check the bundle channel: {ex.Message}";
        }
        finally
        {
            RemasteredChecking = false;
        }
    }

    /// <summary>Fetch the newest bundle of the selected channel, decrypt it
    /// with the unlock password, and replace the installed bundle.</summary>
    [RelayCommand]
    private async Task UpdateRemasteredAsync()
    {
        if (string.IsNullOrWhiteSpace(RemasteredPassword))
        {
            RemasteredUpdateStatus = "Enter the unlock password, then press “Update to latest” again.";
            return;
        }
        RemasteredChecking = true;
        try
        {
            var result = await Task.Run(
                () => RemasteredAccess.TryUpdateBundle(_settings, RemasteredPassword));
            RemasteredUpdateStatus = result;
            if (!result.StartsWith("Wrong password", StringComparison.Ordinal))
                RemasteredPassword = "";
            RemasteredInstalled = FormatRemasteredInstalled();
            var (installedTag, _) = RemasteredAccess.ReadInstalledVersion();
            RemasteredUpToDate = installedTag == RemasteredLatest;
        }
        finally
        {
            RemasteredChecking = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        _settings.RootPath = RootPath.TrimEnd('/', '\\');
        _settings.LinuxPrefixRoot = LinuxPrefixRoot.TrimEnd('/', '\\');
        _settings.ActionOnPlay = ActionOnPlay;
        _settings.FullVersionLog = FullVersionLog;
        _settings.DiscordWebhookUrl = string.IsNullOrWhiteSpace(DiscordWebhookUrl)
            ? null
            : DiscordWebhookUrl.Trim();
        _settings.PlusReplayServer = string.IsNullOrWhiteSpace(PlusReplayServer)
            ? ""
            : PlusReplayServer.Trim();
        _settings.CommunityRepo = CommunityRepo.Trim();
        _settings.RemasteredChannel = RemasteredChannel;
        _settings.RemasteredBundleUrl = string.IsNullOrWhiteSpace(RemasteredBundleUrl)
            ? null
            : RemasteredBundleUrl.Trim();
        _settings.GitHubToken = string.IsNullOrWhiteSpace(GithubToken)
            ? null
            : GithubToken.Trim();
        _settings.Save();
        Status = "Saved.";
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        RootPath = PlatformDefaults.InstallRoot();
        LinuxPrefixRoot = PlatformDefaults.LinuxPrefixRoot();
        ActionOnPlay = ActionOnPlay.StayOpen;
        FullVersionLog = false;
        DiscordWebhookUrl = "";
        PlusReplayServer = "";
        CommunityRepo = "saintromuald/cosmonarchy-shares";
        GithubToken = "";
        Status = "Defaults restored — press Save to keep them.";
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        // Transient GitHub failures (rate limits, dropped connections) used
        // to leave the row stuck on "offline" with no install button at all.
        // Retry a couple of times before giving up.
        UpdateBusy = true;
        for (var attempt = 1; ; attempt++)
        {
            LatestVersion = "…";
            UpdateStatus = $"Checking for updates (current: {CurrentVersion})…";
            var service = new UpdateService();
            var versions = await service.GetVersionsAsync();
            if (versions is not null)
            {
                LatestVersion = versions.LatestReleaseTag ?? "none";
                LatestPrerelease = versions.LatestPrereleaseTag ?? "";
            }
            var info = await service.CheckAsync(CurrentVersion);
            _update = info;
            if (info is null)
            {
                HasUpdate = false;
                CanInstallUpdate = false;
                if (attempt < 3)
                {
                    UpdateStatus = $"Could not check for updates (offline or rate-limited?) — retrying ({attempt}/3)…";
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    continue;
                }
                if (versions is null)
                    LatestVersion = "offline";
                UpdateStatus = "Could not check for updates (offline or rate-limited?) — use the release page.";
            }
            else if (!info.IsNewer)
            {
                HasUpdate = false;
                CanInstallUpdate = false;
                UpdateStatus = $"You are on the latest version ({CurrentVersion}).";
            }
            else
            {
                var kind = SelfUpdater.GetInstallKind();
                HasUpdate = true;
                CanInstallUpdate = info.AssetUrl is not null && kind is InstallKind.LinuxDirectory or InstallKind.WindowsSingleFile;
                UpdateStatus = CanInstallUpdate
                    ? $"Version {info.Tag} is available ({info.AssetName}). Install it right here — the launcher restarts itself."
                    : info.AssetUrl is null
                        ? $"Version {info.Tag} is available, but this release has no file for this platform — use the release page."
                        : $"Version {info.Tag} is available. This install updates elsewhere (AppImage or package manager) — the button downloads {info.AssetName} to your Downloads folder.";
            }
            CanOpenRelease = _update is not null || LatestVersion == "offline";
            UpdateBusy = false;
            return;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_update is not { IsNewer: true } info)
            return;
        if (!CanInstallUpdate)
        {
            await DownloadUpdateAsync();
            return;
        }
        UpdateBusy = true;
        DownloadFraction = -1;
        try
        {
            var progress = new Progress<string>(s =>
            {
                UpdateStatus = s;
                DownloadFraction = HttpDownload.PercentOf(s) is { } percent ? percent / 100 : -1;
            });
            var prepared = await new SelfUpdater().PrepareAsync(SelfUpdater.AppDirectory(), info, progress);
            UpdateStatus = "Update staged — restarting to finish…";
            UpdateBusy = false;
            DownloadFraction = -1;
            SelfUpdater.ApplyAndExit(prepared);
        }
        catch (Exception ex)
        {
            UpdateBusy = false;
            DownloadFraction = -1;
            UpdateStatus = $"Update failed: {ex.Message}";
        }
    }

    /// <summary>Fallback for installs that cannot replace themselves
    /// (AppImage, package manager): download the platform asset into the
    /// Downloads folder and tell the user where it is.</summary>
    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (_update is not { IsNewer: true } info ||
            info.AssetUrl is not { } assetUrl ||
            info.AssetName is not { } assetName)
            return;
        UpdateBusy = true;
        DownloadFraction = -1;
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            var destination = Path.Combine(downloads, assetName);
            var progress = new Progress<string>(s =>
            {
                UpdateStatus = s;
                DownloadFraction = HttpDownload.PercentOf(s) is { } percent ? percent / 100 : -1;
            });
            await HttpDownload.ToFileAsync(
                new HttpClient { Timeout = TimeSpan.FromMinutes(15) },
                assetUrl, destination, progress, $"Downloading {assetName}");
            UpdateStatus = $"Saved {assetName} to your Downloads folder — open it and follow the release notes.";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            UpdateBusy = false;
            DownloadFraction = -1;
        }
    }

    [RelayCommand]
    private void OpenRelease()
    {
        // When the check never returned an update (offline), fall back to the
        // project's latest release page so there is always an install path.
        var url = _update?.Url
            ?? $"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/releases/latest";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateStatus = $"The release page is at {url} ({ex.Message})";
        }
    }
}
