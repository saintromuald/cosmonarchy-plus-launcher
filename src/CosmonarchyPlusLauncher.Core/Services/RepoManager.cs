using System.Diagnostics;
using CosmonarchyPlusLauncher.Core.Models;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

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
    public string EnsureRemoteName() => EnsureRemote(_url);

    /// <summary>Name of the remote that points at <paramref name="url"/>,
    /// adding one when missing. Used when one checkout carries both the fork
    /// and upstream remotes.</summary>
    public string EnsureRemote(string url)
    {
        var repo = Open();
        var remote = repo.Network.Remotes.FirstOrDefault(r =>
            string.Equals(Normalize(r.Url), Normalize(url), StringComparison.OrdinalIgnoreCase));
        if (remote is not null)
            return remote.Name;

        var name = "launcher";
        var n = 1;
        while (repo.Network.Remotes[name] is not null)
            name = $"launcher{n++}";
        repo.Network.Remotes.Add(name, url);
        return name;
    }

    public void Clone(IProgress<string>? progress = null)
    {
        progress?.Report($"Cloning {_url}");
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        Repository.Clone(_url, _dir, new CloneOptions
        {
            RecurseSubmodules = true,
            FetchOptions = { OnTransferProgress = TransferReporter(progress, $"Cloning {_url}") },
        });
    }

    public void Fetch(IProgress<string>? progress = null)
    {
        var name = EnsureRemoteName();
        FetchRemote(name, progress);
    }

    /// <summary>Fetch one configured remote by name (used when a checkout has
    /// both the fork and upstream remotes and we need objects from either).</summary>
    public void FetchRemote(string name, IProgress<string>? progress = null)
    {
        var repo = Open();
        var remote = repo.Network.Remotes[name] ?? throw new InvalidOperationException($"Remote {name} disappeared from {_dir}");
        IEnumerable<string> refspecs = remote.FetchRefSpecs.Any()
            ? remote.FetchRefSpecs.Select(s => s.Specification)
            : new[] { $"+refs/heads/*:refs/remotes/{name}/*" };
        progress?.Report($"Fetching {remote.Url}");
        repo.Network.Fetch(name, refspecs, new FetchOptions
        {
            OnTransferProgress = TransferReporter(progress, $"Fetching {remote.Url}"),
        });
    }

    /// <summary>Throttled git transfer progress: libgit2 reports very often,
    /// so this only forwards a message twice a second or every 256 KiB.
    /// Messages read "Cloning <url>… 128 MB · 2,041 / 4,512 objects" — no
    /// percentage, because git cannot know the transfer size up front.</summary>
    private static TransferProgressHandler TransferReporter(IProgress<string>? progress, string label)
    {
        if (progress is null)
            return _ => false;
        var stopwatch = Stopwatch.StartNew();
        long lastBytes = 0;
        return t =>
        {
            if (t.ReceivedBytes - lastBytes < 256 * 1024 &&
                stopwatch.ElapsedMilliseconds < 500)
                return false;
            stopwatch.Restart();
            lastBytes = t.ReceivedBytes;
            var objects = t.TotalObjects > 0
                ? $"{t.ReceivedObjects:N0} / {t.TotalObjects:N0} objects"
                : $"{t.ReceivedObjects:N0} objects";
            progress.Report($"{label}… {HttpDownload.FormatBytes(t.ReceivedBytes)} · {objects}");
            return false;
        };
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

    /// <summary>Check out a commit without moving any branch. Used for pinned
    /// versions and replay playback: moving the branch would rewrite a
    /// developer's `plus` branch to an upstream or old commit.</summary>
    public void CheckoutCommit(string sha, IProgress<string>? progress = null)
    {
        var repo = Open();
        var commit = repo.Lookup<Commit>(sha)
                     ?? throw new InvalidOperationException($"No such commit: {sha}");
        progress?.Report($"Checking out {commit.Id.Sha[..8]}");
        Commands.Checkout(repo, commit, new CheckoutOptions
        {
            CheckoutModifiers = CheckoutModifiers.Force,
        });
        foreach (var submodule in repo.Submodules)
            repo.Submodules.Update(submodule.Name, new SubmoduleUpdateOptions { Init = true });
    }

    /// <summary>Resolve a full or abbreviated SHA to a commit in this repo.</summary>
    public string? ResolveCommit(string shaPrefix)
    {
        var repo = Open();
        try
        {
            var commit = repo.Lookup<Commit>(shaPrefix);
            return commit?.Id.Sha;
        }
        catch (LibGit2SharpException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="shaPrefix"/> is reachable from any
    /// ref under refs/remotes/<paramref name="remoteName"/>/. That is how the
    /// online list tells the maintainer's Plus builds from upstream ones.</summary>
    public bool IsInRemote(string shaPrefix, string remoteName)
    {
        var repo = Open();
        var refs = repo.Refs
            .Where(r => r.CanonicalName.StartsWith($"refs/remotes/{remoteName}/", StringComparison.Ordinal))
            .Cast<Reference>()
            .ToList();
        if (refs.Count == 0)
            return false;
        var filter = new CommitFilter { IncludeReachableFrom = refs };
        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            if (commit.Sha.StartsWith(shaPrefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
        repo.Network.Fetch(name, new[] { "+refs/*:refs/*" }, new FetchOptions
        {
            OnTransferProgress = TransferReporter(progress, $"Fetching {remote.Url}"),
        });
    }

    private Repository Open() => _repo ??= new Repository(_dir);

    private static string Normalize(string url) => url.TrimEnd('/', '\\');

    public void Dispose()
    {
        _repo?.Dispose();
        _repo = null;
    }
}
