using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.ViewModels;

public partial class PlayViewModel : ViewModelBase
{

    private readonly Core.Settings.AppSettings _settings = AppRuntime.Settings;

    public ObservableCollection<RepoEntry> Repos { get; } = new();

    public ObservableCollection<VersionOption> Versions { get; } = new();

    public IReadOnlyList<Variant> Variants { get; } = [Variant.Original, Variant.Plus, Variant.Netcode];

    public IReadOnlyList<NetcodeRole> Roles { get; } = [NetcodeRole.Host, NetcodeRole.Guest, NetcodeRole.Solo];

    [ObservableProperty]
    private RepoEntry? _selectedRepo;

    [ObservableProperty]
    private VersionOption? _selectedVersion;

    [ObservableProperty]
    private Variant _variant = AppRuntime.Settings.Variant;

    [ObservableProperty]
    private NetcodeRole? _role = AppRuntime.Settings.NetcodeRole ?? NetcodeRole.Solo;

    [ObservableProperty]
    private bool _showUnofficial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _busy;

    [ObservableProperty]
    private string _status = "Select a repository and version, then install or play.";

    public bool CanInstall => !Busy && SelectedRepo is not null && SelectedVersion is not null;

    public PlayViewModel()
    {
        _showUnofficial = _settings.ShowUnofficialRepos;
        RebuildRepos();
        SelectedRepo = RepoCatalog.Find(_settings.SelectedRepoId) ?? Repos.FirstOrDefault();
        LoadVersions();
    }

    partial void OnShowUnofficialChanged(bool value)
    {
        _settings.ShowUnofficialRepos = value;
        RebuildRepos();
    }

    partial void OnVariantChanged(Variant value)
    {
        _settings.Variant = value;
        _settings.Save();
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
            _settings.Save();
        }
        LoadVersions();
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnSelectedVersionChanged(VersionOption? value) => OnPropertyChanged(nameof(CanInstall));

    private void RebuildRepos()
    {
        var selectedId = SelectedRepo?.Id ?? _settings.SelectedRepoId;
        Repos.Clear();
        foreach (var repo in VisibleRepos(ShowUnofficial))
            Repos.Add(repo);
        SelectedRepo = Repos.FirstOrDefault(r => r.Id == selectedId) ?? Repos.FirstOrDefault();
    }

    [RelayCommand]
    private void Refresh() => LoadVersions();

    private void LoadVersions()
    {
        var repo = SelectedRepo;
        if (repo is null)
            return;

        Versions.Clear();
        Versions.Add(new VersionOption("Latest — update to the newest build", null));
        Versions.Add(new VersionOption("Current — keep the installed checkout", "HEAD"));
        SelectedVersion = Versions[0];

        var installRoot = _settings.RootPath;
        var repoDir = Path.Combine(installRoot, repo.BuildDir ?? "Release");
        if (!LibGit2Sharp.Repository.IsValid(repoDir))
            return;

        Busy = true;
        _ = Task.Run(() =>
        {
            try
            {
                using var manager = new RepoManager(repoDir, repo.Url);
                var commits = manager.Log(200).Select(c => new VersionOption(
                    $"{c.Sha[..8]}  {c.Date}  {c.Message}", c.Sha)).ToList();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    foreach (var c in commits)
                        Versions.Add(c);
                    if (SelectedVersion is null)
                        SelectedVersion = Versions[0];
                    Status = $"{commits.Count} versions available.";
                });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = $"Could not read versions: {ex.Message}");
            }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Busy = false);
            }
        });
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        var repo = SelectedRepo;
        if (repo is null)
            return;
        _settings.NetcodeRole = Role;   // persist the effective role (Solo default)
        var sha = SelectedVersion?.Sha is "HEAD" ? null : SelectedVersion?.Sha;
        await RunAsync(async (service, progress, ct) =>
            await service.InstallAsync(sha, progress, ct), repo);
        LoadVersions();
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        var repo = SelectedRepo;
        if (repo is null)
            return;
        _settings.NetcodeRole = Role;   // persist the effective role (Solo default)
        var sha = SelectedVersion?.Sha is "HEAD" ? null : SelectedVersion?.Sha;
        await RunAsync(async (service, progress, ct) =>
            await service.PlayAsync(sha, progress, ct), repo);
        LoadVersions();
    }

    [RelayCommand]
    private async Task UnshallowAsync()
    {
        var repo = SelectedRepo;
        if (repo is null)
            return;
        await RunAsync(async (service, progress, ct) =>
        {
            var repoDir = Path.Combine(service.InstallRoot, repo.BuildDir ?? "Release");
            using var manager = new RepoManager(repoDir, repo.Url);
            await Task.Run(() => manager.Unshallow(progress), ct);
        }, repo);
    }

    private async Task RunAsync(
        Func<InstallService, IProgress<string>, CancellationToken, Task> action,
        RepoEntry repo)
    {
        Busy = true;
        Status = "Working…";
        var progress = new Progress<string>(s => Status = s);
        try
        {
            var service = AppRuntime.CreateInstaller(repo);
            await Task.Run(() => action(service, progress, CancellationToken.None));
            Status = "Done.";
        }
        catch (Exception ex)
        {
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private static IEnumerable<RepoEntry> VisibleRepos(bool includeUnofficial)
    {
        foreach (var repo in RepoCatalog.Official)
            yield return repo;
        if (includeUnofficial)
            foreach (var repo in RepoCatalog.Unofficial)
                yield return repo;
    }
}

