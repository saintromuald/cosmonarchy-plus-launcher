using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Settings;
using LibGit2Sharp;

namespace CosmonarchyPlusLauncher.Core.Services;

public enum ReplayChannel
{
    /// <summary>Upstream No-Frauds Club builds.</summary>
    Original,

    /// <summary>The maintainer's Cosmonarchy Plus fork.</summary>
    Plus,
}

/// <summary>The build a replay needs: channel, repository, checkout directory
/// relative to the install root, and the full commit.</summary>
public sealed record ReplayBuild(ReplayChannel Channel, RepoEntry Repo, string Dir, string FullSha)
{
    public string Label => Channel == ReplayChannel.Plus ? "Cosmonarchy Plus" : "Cosmonarchy OG";
}

/// <summary>Works out which build a ReplayKraken match needs.
///
/// Replays carry the short commit and channel they were recorded on
/// (<c>gameVersion</c>, <c>gameVersionKind</c>). The same channel can come from
/// two repositories: the maintainer's fork (Plus) or upstream (OG). A short SHA
/// belongs to the fork when it is reachable from the fork remote, otherwise it
/// is an upstream build. Playback uses a dedicated checkout per channel
/// (<c>replay-builds/release|prerelease</c>) so watching never disturbs the
/// normal Play install.</summary>
public sealed class ReplayBuildResolver
{
    private readonly AppSettings _settings;

    public ReplayBuildResolver(AppSettings settings) => _settings = settings;

    public static bool IsPrerelease(string kind) =>
        kind.Equals("Prerelease", StringComparison.OrdinalIgnoreCase);

    public static RepoEntry? ForkRepo(bool prerelease) =>
        RepoCatalog.Find(prerelease ? "prerelease-mine" : "release");

    public static RepoEntry? UpstreamRepo(bool prerelease) =>
        RepoCatalog.Find(prerelease ? "prerelease" : "release-upstream");

    /// <summary>Every commit reachable from the fork's remote refs in an
    /// existing checkout. Classification only needs prefixes, so the VM builds
    /// this set once per load instead of probing 11k matches individually.</summary>
    public IReadOnlySet<string> ForkShas(RepoEntry fork, string checkoutDir)
    {
        if (!Repository.IsValid(checkoutDir))
            return new HashSet<string>();
        using var repo = new Repository(checkoutDir);
        var remote = repo.Network.Remotes.FirstOrDefault(r =>
            string.Equals(r.Url.TrimEnd('/', '\\'), fork.Url.TrimEnd('/', '\\'),
                StringComparison.OrdinalIgnoreCase));
        if (remote is null)
            return new HashSet<string>();
        var refs = repo.Refs
            .Where(r => r.CanonicalName.StartsWith($"refs/remotes/{remote.Name}/", StringComparison.Ordinal))
            .Cast<Reference>()
            .ToList();
        if (refs.Count == 0)
            return new HashSet<string>();
        var filter = new CommitFilter { IncludeReachableFrom = refs };
        return repo.Commits.QueryBy(filter)
            .Select(c => c.Sha)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Classify a build as Plus or OG using the already-installed
    /// Release/Prerelease checkout (read-only, no clone, no fetch). Missing
    /// checkouts or unknown SHAs fall back to OG.</summary>
    public ReplayChannel Classify(string shaPrefix, string kind)
    {
        var prerelease = IsPrerelease(kind);
        var fork = ForkRepo(prerelease);
        var dir = Path.Combine(_settings.RootPath, prerelease ? "Prerelease" : "Release");
        if (fork is null || !Repository.IsValid(dir))
            return ReplayChannel.Original;
        try
        {
            using var repo = new RepoManager(dir, fork.Url);
            var remote = repo.FindMatchingRemoteName();
            return remote is not null && repo.IsInRemote(shaPrefix, remote)
                ? ReplayChannel.Plus
                : ReplayChannel.Original;
        }
        catch (LibGit2SharpException)
        {
            return ReplayChannel.Original;
        }
    }

    /// <summary>Resolve a match to a build, fetching the fork and upstream
    /// remotes into the replay store when needed. Clones the store on first
    /// use (that is the only slow first-time step).</summary>
    public Task<ReplayBuild> ResolveAsync(
        OnlineMatch match, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var prerelease = IsPrerelease(match.GameVersionKind);
        var fork = ForkRepo(prerelease);
        var upstream = UpstreamRepo(prerelease);
        var primary = fork ?? upstream
                      ?? throw new InvalidOperationException("No game repository configured for this replay.");

        // One working tree per channel, like CosmoWatch: watching switches the
        // channel checkout to the replay's commit (detached - no branch moves),
        // and Play on the Play page puts the tracked branch back.
        var dirName = prerelease ? "Prerelease" : "Release";
        var dir = Path.Combine(_settings.RootPath, dirName);
        using var repo = new RepoManager(dir, primary.Url);
        if (!repo.IsInstalled())
        {
            progress?.Report($"Preparing the {dirName} builds (first use downloads the repository)…");
            repo.Clone(progress);
        }

        var forkRemote = fork is null ? null : repo.EnsureRemote(fork.Url);
        var upstreamRemote = upstream is null ? null : repo.EnsureRemote(upstream.Url);

        TryFetch(repo, forkRemote, progress);
        if (upstreamRemote is not null && upstreamRemote != forkRemote)
            TryFetch(repo, upstreamRemote, progress);

        var full = repo.ResolveCommit(match.GameVersion);
        if (full is null)
            throw new InvalidOperationException(
                $"No {match.GameVersionKind} build {match.GameVersion} in the {dirName} repositories. " +
                "It may be from a build that is no longer published.");

        var plus = forkRemote is not null && repo.IsInRemote(full, forkRemote);
        var chosen = plus ? fork! : upstream!;
        progress?.Report(
            $"{(plus ? "Cosmonarchy Plus" : "Cosmonarchy OG")} build {full[..8]} ({match.VersionLabel})");
        return Task.FromResult(new ReplayBuild(
            plus ? ReplayChannel.Plus : ReplayChannel.Original,
            chosen,
            dirName,
            full));
    }

    private static void TryFetch(RepoManager repo, string? remote, IProgress<string>? progress)
    {
        if (remote is null)
            return;
        try
        {
            repo.FetchRemote(remote, progress);
        }
        catch (Exception ex)
        {
            // Offline or remote down: use whatever the store already has.
            progress?.Report($"Could not fetch {remote} ({ex.Message}); using local builds.");
        }
    }
}
