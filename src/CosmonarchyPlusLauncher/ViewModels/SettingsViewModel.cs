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
    private string _status = "";

    [ObservableProperty]
    private string _updateStatus = "";

    [RelayCommand]
    private void Save()
    {
        _settings.RootPath = RootPath.TrimEnd('/', '\\');
        _settings.LinuxPrefixRoot = LinuxPrefixRoot.TrimEnd('/', '\\');
        _settings.ActionOnPlay = ActionOnPlay;
        _settings.FullVersionLog = FullVersionLog;
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
        Status = "Defaults restored — press Save to keep them.";
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateStatus = $"Checking for updates (current: {CurrentVersion})…";
        var info = await new UpdateService().CheckAsync(CurrentVersion);
        if (info is null)
        {
            UpdateStatus = "Could not check for updates.";
            return;
        }

        if (info.IsNewer)
        {
            UpdateStatus = $"Version {info.Tag} is available.";
            try
            {
                Process.Start(new ProcessStartInfo(info.Url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                UpdateStatus = $"Version {info.Tag} is available at {info.Url} ({ex.Message})";
            }
        }
        else
        {
            UpdateStatus = "You are on the latest version.";
        }
    }
}
