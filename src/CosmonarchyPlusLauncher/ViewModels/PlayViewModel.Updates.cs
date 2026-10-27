using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>The Play page's launcher self-update section: a prominent
/// "Update Cosmonarchy Launcher" button beside the latest released version.
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

    /// <summary>True when a newer release exists and this install can apply
    /// it in place (directory install on Linux, single-file exe on Windows).</summary>
    [ObservableProperty]
    private bool _updateAvailable;

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

    /// <summary>What the button offers, beside the latest version text.</summary>
    public string UpdateHint => UpdateAvailable
        ? $"An update is available — you are on {LauncherVersion}."
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
    partial void OnLatestVersionChanged(string value) => OnPropertyChanged(nameof(UpdateHint));
    partial void OnLatestPrereleaseChanged(string value) => OnPropertyChanged(nameof(UpdateHint));

    [RelayCommand]
    private async Task CheckForLauncherUpdateAsync()
    {
        UpdateBusy = true;
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
            if (versions is null)
            {
                LatestVersion = "offline";
                UpdateStatus = "Could not check for updates (offline or rate-limited?).";
            }
            UpdateAvailable = false;
        }
        else if (!info.IsNewer)
        {
            UpdateAvailable = false;
            UpdateStatus = $"You are on the latest version ({LauncherVersion}).";
        }
        else
        {
            var kind = SelfUpdater.GetInstallKind();
            UpdateAvailable = info.AssetUrl is not null
                && kind is InstallKind.LinuxDirectory or InstallKind.WindowsSingleFile;
            UpdateStatus = UpdateAvailable
                ? $"Version {info.Tag} is available ({info.AssetName}). Install it right here — the launcher restarts itself."
                : $"Version {info.Tag} is available, but this install updates elsewhere (AppImage or package manager) — use the release page.";
        }
        UpdateBusy = false;
    }

    [RelayCommand]
    private async Task InstallLauncherUpdateAsync()
    {
        if (_playUpdate is not { IsNewer: true } info || !UpdateAvailable)
            return;
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

    [RelayCommand]
    private void OpenLauncherRelease()
    {
        if (_playUpdate is not { } info)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(info.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Version {info.Tag} is available at {info.Url} ({ex.Message})";
        }
    }
}
