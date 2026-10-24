using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>The community share board: everything published to the public
/// GitHub repo, with the sharer's Discord name when they were logged in.</summary>
public partial class CommunityViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppRuntime.Settings;
    private readonly ReplayLibrary _library = AppRuntime.Library;
    private readonly CommunityService _community = new();

    public ObservableCollection<CommunityShare> Shares { get; } = new();

    [ObservableProperty] private CommunityShare? _selectedShare;
    [ObservableProperty] private string _status = "Loading the community board…";
    [ObservableProperty] private bool _busy;

    public string BoardLabel => $"Board: {_settings.CommunityRepo}";

    public string WhoAmILabel => AppRuntime.CurrentUser is { } user
        ? $"Logged in as {user.Name} — your published shares carry this name."
        : "Not logged in — your published shares appear as “anonymous”. Log in on the Account page to attach your Discord name.";

    /// <summary>Unpublishing needs your GitHub token and an exact Discord-id
    /// match, so only the author can take a share down.</summary>
    public bool CanUnpublishSelected =>
        SelectedShare is { DiscordId.Length: > 0 } share &&
        AppRuntime.CurrentUser is { } user &&
        share.DiscordId.Equals(user.DiscordId, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(_settings.GitHubToken);

    public CommunityViewModel()
    {
        AppRuntime.CurrentUserChanged += OnCurrentUserChanged;
        _ = RefreshAsync();
    }

    private void OnCurrentUserChanged()
    {
        OnPropertyChanged(nameof(WhoAmILabel));
        OnPropertyChanged(nameof(CanUnpublishSelected));
    }

    partial void OnSelectedShareChanged(CommunityShare? value) =>
        OnPropertyChanged(nameof(CanUnpublishSelected));

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Busy = true;
        Status = "Loading the community board…";
        try
        {
            var shares = await _community.ListSharesAsync(_settings.CommunityRepo);
            Shares.Clear();
            foreach (var share in shares)
                Shares.Add(share);
            Status = shares.Count == 0
                ? $"The board {_settings.CommunityRepo} is empty — publish a favourites list and it becomes the first entry. (If the repo does not exist yet, create it as a public GitHub repo first.)"
                : $"{shares.Count} public share(s) from the community.";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task ImportSelectedAsync()
    {
        if (SelectedShare is not { } share)
            return;
        Busy = true;
        try
        {
            var json = await _community.FetchShareAsync(_settings.CommunityRepo, share.FileName);
            var imported = ReplaySharing.ParseImport(json);
            if (imported is null)
            {
                Status = $"“{share.Name}” could not be read (unrecognised payload).";
                return;
            }
            var (favourites, notes, notesUpdated, lists) = _library.Merge(imported);
            AppRuntime.SaveLibrary();
            var by = share.AuthorLabel == "anonymous" ? "" : $" by {share.AuthorLabel}";
            Status = $"Imported “{share.Name}”{by}: {favourites} favourite(s), {notes} note(s), {notesUpdated} updated, {lists} list(s).";
        }
        catch (Exception ex)
        {
            Status = $"Could not import “{share.Name}”: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task UnpublishSelectedAsync()
    {
        if (SelectedShare is not { } share)
            return;
        if (string.IsNullOrWhiteSpace(_settings.GitHubToken))
        {
            Status = "Unpublishing needs your GitHub token in Settings.";
            return;
        }
        Busy = true;
        try
        {
            await _community.UnpublishAsync(_settings.CommunityRepo, _settings.GitHubToken, share);
            Shares.Remove(share);
            SelectedShare = Shares.FirstOrDefault();
            Status = $"Unpublished “{share.Name}” from the community board.";
        }
        catch (Exception ex)
        {
            Status = $"Could not unpublish: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void OpenBoard()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://github.com/{_settings.CommunityRepo}")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status = $"Could not open the board: {ex.Message}";
        }
    }
}
