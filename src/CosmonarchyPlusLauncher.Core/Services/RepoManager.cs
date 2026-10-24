using CosmonarchyPlusLauncher.Core.Models;
using LibGit2Sharp;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Clone/fetch/reset/log for a single repository. Wraps
/// LibGit2Sharp the same way the original launcher does, but written fresh.</summary>
public sealed class RepoManager : IDisposable
{
    private readonly string _dir;
    private readonly string _url;
    private Repository? _repo;

    public RepoManager(string dir, string url)
    {
        _dir = dir;
        _url = url;
    }

    public string Url => _url;
    public string Dir => _dir;

    public bool IsInstalled()
    {
        if (!Repository.IsValid(_dir))
            return false;
        using var repo = new Repository(_dir);
        return repo.Network.Remotes["origin"] is { } remote &&
               string.Equals(Normalize(remote.Url), Normalize(_url), StringComparison.OrdinalIgnoreCase);
    }

    public void Clone(IProgress<string>? progress = null)
    {
        progress?.Report($"Cloning {_url}");
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        Repository.Clone(_url, _dir, new CloneOptions
        {
            RecurseSubmodules = true,
        });
    }

    public void Fetch(IProgress<string>? progress = null)
    {
        progress?.Report($"Fetching {_url}");
        using var repo = Open();
        foreach (var remote in repo.Network.Remotes)
            repo.Network.Fetch(remote.Url, Array.Empty<string>());
    }

    public void ResetTo(string shaOrRef, IProgress<string>? progress = null)
    {
        using var repo = Open();
        var commit = repo.Lookup<Commit>(shaOrRef)
                     ?? throw new InvalidOperationException($"No such commit or ref: {shaOrRef}");
        repo.Reset(ResetMode.Hard, commit);
        progress?.Report($"Reset to {commit.Id.Sha[..8]}");
        foreach (var submodule in repo.Submodules)
            repo.Submodules.Update(submodule.Name, new SubmoduleUpdateOptions { Init = true });
    }

    public IReadOnlyList<CommitInfo> Log(int maxCount = 200)
    {
        using var repo = Open();
        return repo.Commits.Take(maxCount).Select(c => new CommitInfo(
            c.Id.Sha,
            c.MessageShort,
            c.Author.When.ToString("yyyy-MM-dd HH:mm"))).ToList();
    }

    public CommitInfo? NewestCommit()
    {
        using var repo = Open();
        var c = repo.Commits.FirstOrDefault();
        return c is null ? null : new CommitInfo(c.Id.Sha, c.MessageShort, c.Author.When.ToString("yyyy-MM-dd HH:mm"));
    }

    public bool HasLocalChanges()
    {
        using var repo = Open();
        return repo.RetrieveStatus(new StatusOptions()).Where(
            s => s.State != FileStatus.Ignored && !s.State.HasFlag(FileStatus.NewInWorkdir)).Any();
    }

    /// <summary>Full history for the shallow-log setting: fetch every ref and
    /// drop the shallow boundary if libgit2 allows it.</summary>
    public void Unshallow(IProgress<string>? progress = null)
    {
        progress?.Report("Fetching full version log");
        using var repo = Open();
        foreach (var remote in repo.Network.Remotes)
            repo.Network.Fetch(remote.Url, new[] { "+refs/*:refs/*" }, new FetchOptions());
    }

    private Repository Open() => _repo ??= new Repository(_dir);

    private static string Normalize(string url) => url.TrimEnd('/', '\\');

    public void Dispose()
    {
        _repo?.Dispose();
        _repo = null;
    }
}
