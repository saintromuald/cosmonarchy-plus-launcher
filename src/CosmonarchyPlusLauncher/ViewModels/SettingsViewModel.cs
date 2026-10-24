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

    /// <summary>Unlock fields for the hidden Remastered launch.</summary>
    [ObservableProperty]
    private string _remasteredPassword = "";

    [ObservableProperty]
    private string _remasteredBundleUrl = AppRuntime.Settings.RemasteredBundleUrl ?? "";

    [ObservableProperty]
    private string _remasteredStatus =
        RemasteredAccess.IsUnlocked(AppRuntime.Settings)
            ? "Unlocked on this machine."
            : (RemasteredAccess.IsBundleInstalled ? "Locked." : "Locked (bundle not installed).");

    [ObservableProperty]
    private string _communityRepo = AppRuntime.Settings.CommunityRepo;

    [ObservableProperty]
    private string _githubToken = AppRuntime.Settings.GitHubToken ?? "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    private bool _updateBusy;

    [ObservableProperty]
    private bool _canInstallUpdate;

    [ObservableProperty]
    private bool _canOpenRelease;

    private UpdateInfo? _update;

    [RelayCommand]
    private void UnlockRemastered()
    {
        RemasteredStatus = RemasteredAccess.TryUnlock(_settings, RemasteredPassword);
        if (!RemasteredStatus.StartsWith("Wrong password", StringComparison.Ordinal))
            RemasteredPassword = "";
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
        _settings.CommunityRepo = CommunityRepo.Trim();
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
        CommunityRepo = "saintromuald/cosmonarchy-shares";
        GithubToken = "";
        Status = "Defaults restored — press Save to keep them.";
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateBusy = true;
        UpdateStatus = $"Checking for updates (current: {CurrentVersion})…";
        var info = await new UpdateService().CheckAsync(CurrentVersion);
        _update = info;
        if (info is null)
        {
            UpdateStatus = "Could not check for updates (offline or rate-limited?).";
        }
        else if (!info.IsNewer)
        {
            UpdateStatus = $"You are on the latest version ({CurrentVersion}).";
        }
        else
        {
            var kind = SelfUpdater.GetInstallKind();
            CanInstallUpdate = info.AssetUrl is not null && kind is InstallKind.LinuxDirectory or InstallKind.WindowsSingleFile;
            UpdateStatus = CanInstallUpdate
                ? $"Version {info.Tag} is available ({info.AssetName}). Install it right here — the launcher restarts itself."
                : $"Version {info.Tag} is available. This install updates elsewhere (AppImage or package manager) — use the release page.";
        }
        CanOpenRelease = _update is not null;
        UpdateBusy = false;
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_update is not { IsNewer: true } info)
            return;
        UpdateBusy = true;
        try
        {
            var progress = new Progress<string>(s => UpdateStatus = s);
            var prepared = await new SelfUpdater().PrepareAsync(SelfUpdater.AppDirectory(), info, progress);
            UpdateStatus = "Update staged — restarting to finish…";
            UpdateBusy = false;
            SelfUpdater.ApplyAndExit(prepared);
        }
        catch (Exception ex)
        {
            UpdateBusy = false;
            UpdateStatus = $"Update failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenRelease()
    {
        if (_update is not { } info)
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
