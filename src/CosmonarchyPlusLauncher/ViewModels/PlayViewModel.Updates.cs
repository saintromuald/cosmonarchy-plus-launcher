using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>The Play page's launcher self-update section: a prominent
/// "Install Update" button beside the latest released version.
/// Mirrors the Settings page's updater (the self-update is the same
/// `UpdateService`/`SelfUpdater` pair); the Play tab is where it is most
/// likely to be noticed.</summary>
public partial class PlayViewModel
{
    public string LauncherVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "dev";

    /// <summary>The newest released launcher version ("checking…" while the
    /// startup check runs, "offline" when GitHub could not be reached).</summary>
    [ObservableProperty]
    private string _latestVersion = "checking…";

    /// <summary>The newest prerelease tag, or "" when no prerelease exists.
    /// Shown beside the stable number for people who want to ride ahead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrerelease))]
    [NotifyPropertyChangedFor(nameof(PrereleaseLabel))]
    private string _latestPrerelease = "";

    public bool HasPrerelease => LatestPrerelease.Length > 0;

    public string PrereleaseLabel => $"prerelease: {LatestPrerelease}";

    /// <summary>True when a newer release exists than the one running. This
    /// is what shows and enables the install button; when the install cannot
    /// replace itself the button downloads the platform asset instead
    /// (see <see cref="CanInstallInPlace"/> and <see cref="InstallLabel"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallLabel))]
    [NotifyPropertyChangedFor(nameof(CanOpenReleasePage))]
    private bool _updateAvailable;

    /// <summary>True when this install can apply the update in place
    /// (directory install on Linux, single-file exe on Windows).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallLabel))]
    [NotifyPropertyChangedFor(nameof(UpdateHint))]
    private bool _canInstallInPlace;

    /// <summary>What the big button says: it installs in place when the
    /// install kind allows it, and downloads the asset to the Downloads
    /// folder otherwise.</summary>
    public string InstallLabel => CanInstallInPlace || !UpdateAvailable
        ? "Install Update"
        : "Download Update";

    /// <summary>The release page is the escape hatch whenever the banner has
    /// something to say: an update exists, or the check could not reach
    /// GitHub at all.</summary>
    public bool CanOpenReleasePage => UpdateAvailable || LatestVersion == "offline";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBarIndeterminate))]
    private bool _updateBusy;

    /// <summary>Download progress as a fraction (0-1), or -1 while nothing
    /// is downloading. The update status carries "NN%"; this feeds the bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatePercent))]
    [NotifyPropertyChangedFor(nameof(UpdateBarIndeterminate))]
    private double _updateFraction = -1;

    public double UpdatePercent => UpdateFraction < 0 ? 0 : UpdateFraction * 100;

    public bool UpdateBarIndeterminate => UpdateBusy && UpdateFraction < 0;

    [ObservableProperty]
    private string _updateStatus = "";

    /// <summary>What the banner text offers, beside the latest version text.</summary>
    public string UpdateHint => UpdateAvailable
        ? (CanInstallInPlace
            ? $"An update is available — you are on {LauncherVersion}."
            : UpdateStatus)
        : LatestVersion.Length > 0 && LatestVersion != "checking…" && LatestVersion != "offline"
            ? (LauncherVersion == LatestVersion
                ? "You are on the latest version"
                : $"Newer than the latest stable — you are on {LauncherVersion}.")
            : UpdateStatus.Length > 0
                ? UpdateStatus
                : "";

    private UpdateInfo? _playUpdate;

    partial void OnUpdateStatusChanged(string value) => OnPropertyChanged(nameof(UpdateHint));
    partial void OnUpdateAvailableChanged(bool value) => OnPropertyChanged(nameof(UpdateHint));
    partial void OnLatestVersionChanged(string value)
    {
        OnPropertyChanged(nameof(UpdateHint));
        OnPropertyChanged(nameof(CanOpenReleasePage));
    }
    partial void OnLatestPrereleaseChanged(string value) => OnPropertyChanged(nameof(UpdateHint));

    [RelayCommand]
    private async Task CheckForLauncherUpdateAsync()
    {
        // Transient GitHub failures (rate limits, dropped connections) used
        // to leave the banner stuck on "offline" with no install button at
        // all. Retry a couple of times before giving up so the banner heals
        // itself without a manual "Check now".
        UpdateBusy = true;
        for (var attempt = 1; ; attempt++)
        {
            LatestVersion = "checking…";
            UpdateStatus = $"Checking for updates (you are on {LauncherVersion})…";
            var service = new UpdateService();
            // The two channels, for the version display (stable is what "latest"
            // means for the update decision; the prerelease rides alongside).
            var versions = await service.GetVersionsAsync();
            if (versions is not null)
            {
                LatestVersion = versions.LatestReleaseTag ?? "none";
                LatestPrerelease = versions.LatestPrereleaseTag ?? "";
            }
            var info = await service.CheckAsync(LauncherVersion);
            _playUpdate = info;
            if (info is null)
            {
                UpdateAvailable = false;
                CanInstallInPlace = false;
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
                UpdateAvailable = false;
                CanInstallInPlace = false;
                UpdateStatus = $"You are on the latest version ({LauncherVersion}).";
            }
            else
            {
                var kind = SelfUpdater.GetInstallKind();
                UpdateAvailable = true;
                CanInstallInPlace = info.AssetUrl is not null
                    && kind is InstallKind.LinuxDirectory or InstallKind.WindowsSingleFile;
                UpdateStatus = CanInstallInPlace
                    ? $"Version {info.Tag} is available ({info.AssetName}). Install it right here — the launcher restarts itself."
                    : info.AssetUrl is null
                        ? $"Version {info.Tag} is available, but this release has no file for this platform — use the release page."
                        : $"Version {info.Tag} is available. This install updates elsewhere (AppImage or package manager) — the button downloads {info.AssetName} to your Downloads folder.";
            }
            UpdateBusy = false;
            return;
        }
    }

    [RelayCommand]
    private async Task InstallLauncherUpdateAsync()
    {
        if (_playUpdate is not { IsNewer: true } info || !UpdateAvailable)
            return;
        if (!CanInstallInPlace)
        {
            await DownloadLauncherUpdateAsync();
            return;
        }
        UpdateBusy = true;
        UpdateFraction = -1;
        try
        {
            var progress = new Progress<string>(s =>
            {
                UpdateStatus = s;
                UpdateFraction = HttpDownload.PercentOf(s) is { } percent ? percent / 100 : -1;
            });
            var prepared = await new SelfUpdater().PrepareAsync(SelfUpdater.AppDirectory(), info, progress);
            UpdateStatus = "Update staged — restarting to finish…";
            UpdateBusy = false;
            UpdateFraction = -1;
            SelfUpdater.ApplyAndExit(prepared);
        }
        catch (Exception ex)
        {
            UpdateBusy = false;
            UpdateFraction = -1;
            UpdateStatus = $"Update failed: {ex.Message}";
        }
    }

    /// <summary>Fallback for installs that cannot replace themselves
    /// (AppImage, package manager): download the platform asset into the
    /// Downloads folder and tell the user where it is.</summary>
    private async Task DownloadLauncherUpdateAsync()
    {
        if (_playUpdate is not { IsNewer: true } info ||
            info.AssetUrl is not { } assetUrl ||
            info.AssetName is not { } assetName)
            return;
        UpdateBusy = true;
        UpdateFraction = -1;
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            var destination = Path.Combine(downloads, assetName);
            var progress = new Progress<string>(s =>
            {
                UpdateStatus = s;
                UpdateFraction = HttpDownload.PercentOf(s) is { } percent ? percent / 100 : -1;
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
            UpdateFraction = -1;
        }
    }

    [RelayCommand]
    private void OpenLauncherRelease()
    {
        // When the check never returned an update (offline), fall back to the
        // project's latest release page so there is always an install path.
        var url = _playUpdate?.Url
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
