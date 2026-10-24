using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

public partial class AccountViewModel : ViewModelBase
{
    private readonly Core.Settings.AppSettings _settings = AppRuntime.Settings;

    [ObservableProperty]
    private User? _user;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string _status = "";

    public bool IsLoggedIn => !string.IsNullOrEmpty(_settings.AccountToken);

    public string DiscordDisplay => User is null ? "" :
        string.IsNullOrEmpty(User.DiscordId) ? "(not linked)" : User.DiscordId;

    public AccountViewModel()
    {
        if (IsLoggedIn)
            _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        Busy = true;
        Status = "Requesting login…";
        try
        {
            var request = await AppRuntime.Account.StartLoginAsync();
            OpenBrowser(request.AuthUrl);
            Status = "Complete the login in your browser; this window will pick it up.";
            var token = await AppRuntime.Account.PollLoginAsync(request.RequestId, TimeSpan.FromSeconds(1.5));
            if (string.IsNullOrEmpty(token))
            {
                Status = "Login cancelled.";
                return;
            }

            _settings.AccountToken = token;
            _settings.Save();
            OnPropertyChanged(nameof(IsLoggedIn));
            await RefreshAsync();
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
    private async Task RefreshAsync()
    {
        if (!IsLoggedIn)
            return;
        Busy = true;
        try
        {
            User = await AppRuntime.Account.LoadUserAsync(_settings.AccountToken!);
            AppRuntime.SetCurrentUser(User);
            OnPropertyChanged(nameof(DiscordDisplay));
            Status = "";
        }
        catch
        {
            Status = "Could not load profile; the token may have expired. Log out and in again.";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Logout()
    {
        _settings.AccountToken = null;
        _settings.Save();
        User = null;
        AppRuntime.SetCurrentUser(null);
        Status = "";
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(DiscordDisplay));
    }

    [RelayCommand]
    private void OpenSite() => OpenBrowser("https://cosmonarchy.com/");

    [RelayCommand]
    private void OpenDiscord() => OpenBrowser("https://discord.gg/s5SKBmY");

    [RelayCommand]
    private void OpenChanges() => OpenBrowser(
        "https://github.com/saintromuald/cosmonarchy-plus/blob/main/" +
        "Differences%20between%20Cosmonarchy%20OG%20and%20Cosmonarchy%20Plus.md");

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not open {url}: {ex.Message}");
        }
    }
}
