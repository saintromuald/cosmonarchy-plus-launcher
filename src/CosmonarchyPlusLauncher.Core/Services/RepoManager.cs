using CosmonarchyPlusLauncher.Core.Models;
using LibGit2Sharp;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Clone/fetch/reset/log for a single repository. Wraps
/// LibGit2Sharp. A repo counts as installed when ANY remote matches the
/// desired URL, because existing installs often keep upstream as `origin`
/// and the user's fork as a second remote (`mine`).</summary>
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
        return FindMatchingRemote(repo) is not null;
    }

    /// <summary>True when the clone has truncated history (git shallow).</summary>
    public bool IsShallow()
    {
        using var repo = new Repository(_dir);
        return repo.Info.IsShallow == true;
    }

    /// <summary>Name of the remote that points at our URL, or null.</summary>
    public string? FindMatchingRemoteName()
    {
        var repo = Open();
        return FindMatchingRemote(repo)?.Name;
    }

    private Remote? FindMatchingRemote(Repository repo) =>
        repo.Network.Remotes.FirstOrDefault(r =>
            string.Equals(Normalize(r.Url), Normalize(_url), StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the remote to use for fetch/reset, adding one named
    /// after the repo if the URL is not configured yet.</summary>
    public string EnsureRemoteName()
    {
        var repo = Open();
        var remote = FindMatchingRemote(repo);
        if (remote is not null)
            return remote.Name;

        var name = "launcher";
        var n = 1;
        while (repo.Network.Remotes[name] is not null)
            name = $"launcher{n++}";
        repo.Network.Remotes.Add(name, _url);
        return name;
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
        var name = EnsureRemoteName();
        var repo = Open();
        var remote = repo.Network.Remotes[name] ?? throw new InvalidOperationException($"Remote {name} disappeared from {_dir}");
        IEnumerable<string> refspecs = remote.FetchRefSpecs.Any()
            ? remote.FetchRefSpecs.Select(s => s.Specification)
            : new[] { $"+refs/heads/*:refs/remotes/{name}/*" };
        progress?.Report($"Fetching {remote.Url}");
        repo.Network.Fetch(name, refspecs);
    }

    public void ResetTo(string shaOrRef, IProgress<string>? progress = null)
    {
        var repo = Open();
        var commit = repo.Lookup<Commit>(shaOrRef)
                     ?? throw new InvalidOperationException($"No such commit or ref: {shaOrRef}");
        repo.Reset(ResetMode.Hard, commit);
        progress?.Report($"Reset to {commit.Id.Sha[..8]}");
        foreach (var submodule in repo.Submodules)
            repo.Submodules.Update(submodule.Name, new SubmoduleUpdateOptions { Init = true });
    }

    /// <summary>Check out <paramref name="branch"/> and fast-forward it to
    /// <paramref name="remoteName"/>/<paramref name="branch"/>.
    ///
    /// A plain Reset(Hard, "remote/branch") moves whatever branch is currently
    /// checked out, so updating the Release install while its `plus` branch was
    /// checked out used to rewrite `plus` to the mirror's `main` snapshot. This
    /// checks the intended branch out first, and creates it when missing.</summary>
    public void CheckoutBranch(string branch, string remoteName, IProgress<string>? progress = null)
    {
        var repo = Open();
        var remoteRef = repo.Refs[$"refs/remotes/{remoteName}/{branch}"]
                        ?? throw new InvalidOperationException(
                            $"Remote {remoteName} has no branch {branch} in {_dir}");
        var commit = repo.Lookup<Commit>(remoteRef.TargetIdentifier)
                     ?? throw new InvalidOperationException($"Cannot resolve {remoteName}/{branch}");

        var local = repo.Branches[branch];
        if (local is null)
        {
            local = repo.CreateBranch(branch, commit);
            repo.Branches.Update(local, b => b.TrackedBranch = remoteRef.CanonicalName);
            progress?.Report($"Created {branch} tracking {remoteName}/{branch}");
        }

        progress?.Report($"Checking out {branch} ({commit.Id.Sha[..8]})");
        Commands.Checkout(repo, local, new CheckoutOptions
        {
            CheckoutModifiers = CheckoutModifiers.Force,
        });
        repo.Reset(ResetMode.Hard, commit);
        foreach (var submodule in repo.Submodules)
            repo.Submodules.Update(submodule.Name, new SubmoduleUpdateOptions { Init = true });
    }

    public IReadOnlyList<CommitInfo> Log(int maxCount = 200)
    {
        var repo = Open();
        var result = new List<CommitInfo>();
        try
        {
            foreach (var c in repo.Commits.Take(maxCount))
            {
                result.Add(new CommitInfo(c.Id.Sha, c.MessageShort,
                    c.Author.When.ToString("yyyy-MM-dd HH:mm")));
            }
        }
        catch (LibGit2SharpException)
        {
            // Partial history (missing parent objects after an interrupted
            // clone): keep everything that enumerated up to the gap.
        }
        return result;
    }

    public CommitInfo? NewestCommit()
    {
        var repo = Open();
        // Resolve HEAD directly: walking repo.Commits dies on broken
        // history (a missing parent object), which some checkouts have.
        var target = repo.Refs.Head?.TargetIdentifier;
        if (target is null)
            return null;
        var c = repo.Lookup<Commit>(target);
        return c is null ? null : new CommitInfo(c.Id.Sha, c.MessageShort,
            c.Author.When.ToString("yyyy-MM-dd HH:mm"));
    }

    public bool HasLocalChanges()
    {
        var repo = Open();
        return repo.RetrieveStatus(new StatusOptions()).Where(
            s => s.State != FileStatus.Ignored && !s.State.HasFlag(FileStatus.NewInWorkdir)).Any();
    }

    /// <summary>Full history for the shallow-log setting: fetch every ref and
    /// drop the shallow boundary if libgit2 allows it.</summary>
    public void Unshallow(IProgress<string>? progress = null)
    {
        var name = EnsureRemoteName();
        progress?.Report("Fetching full version log");
        var repo = Open();
        var remote = repo.Network.Remotes[name] ?? throw new InvalidOperationException($"Remote {name} disappeared from {_dir}");
        repo.Network.Fetch(name, new[] { "+refs/*:refs/*" }, new FetchOptions());
    }

    private Repository Open() => _repo ??= new Repository(_dir);

    private static string Normalize(string url) => url.TrimEnd('/', '\\');

    public void Dispose()
    {
        _repo?.Dispose();
        _repo = null;
    }
}
