using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
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
            // The backend always uses stars.veeq7.com; try that first.
            var token = await TryLoginAsync(request.AuthUrl, request.RequestId);
            if (string.IsNullOrEmpty(token))
            {
                // The raw URL failed (likely Discord rejected the staging
                // redirect_uri). Retry with the production domain.
                var fixedUrl = AccountService.FixAuthUrl(request.RawAuthUrl);
                if (fixedUrl != request.AuthUrl)
                {
                    Status = "First attempt failed; retrying with a different callback…";
                    var retryRequest = await AppRuntime.Account.StartLoginAsync();
                    token = await TryLoginAsync(fixedUrl, retryRequest.RequestId);
                }
            }

            if (string.IsNullOrEmpty(token))
            {
                Status = "Login cancelled or timed out.";
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

    private static async Task<string?> TryLoginAsync(string authUrl, string requestId)
    {
        OpenBrowser(authUrl);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            return await AppRuntime.Account.PollLoginAsync(requestId, TimeSpan.FromSeconds(1.5), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null; // poll failed; caller will retry with fallback URL
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
