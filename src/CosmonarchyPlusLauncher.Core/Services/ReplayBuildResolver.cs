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
/// is an upstream build. Playback uses the per-channel checkout
/// (<c>Release</c>/<c>Prerelease</c>, or <c>Original</c> for games the original
/// client recorded without a channel) so watching never disturbs the normal
/// Play install.
///
/// The original (pre-split) client recorded no <c>gameVersionKind</c>. Those
/// games can come from any published source, so they are resolved against all
/// of them: the ordinary Release/Prerelease checkouts first (their histories
/// share most of the original commits, so this is usually free), then the
/// original repository — which carries the pre-split history and, since the
/// 2026-09-25 merge, the Release and Prerelease histories too — plus the older
/// published sources (the 2023 old-release and nightly archives) as additional
/// remotes. This mirrors the sources Fraud Launcher 2 can backdate to.</summary>
public sealed class ReplayBuildResolver
{
    private readonly AppSettings _settings;

    public ReplayBuildResolver(AppSettings settings) => _settings = settings;

    public static bool IsPrerelease(string kind) =>
        kind.Equals("Prerelease", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the match has no usable channel: the original client
    /// recorded an empty kind, and unknown future kinds should also resolve
    /// across every source rather than fail.</summary>
    public static bool IsOriginal(string kind) =>
        !(IsPrerelease(kind) || kind.Equals("Release", StringComparison.OrdinalIgnoreCase));

    public static RepoEntry? ForkRepo(bool prerelease) =>
        RepoCatalog.Find(prerelease ? "prerelease-mine" : "release");

    public static RepoEntry? UpstreamRepo(bool prerelease) =>
        RepoCatalog.Find(prerelease ? "prerelease" : "release-upstream");

    public static RepoEntry? OriginalRepo() => RepoCatalog.Find("original");

    public static RepoEntry? OldReleaseRepo() => RepoCatalog.Find("old-release");

    public static RepoEntry? NightlyRepo() => RepoCatalog.Find("nightly");

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
    public async Task<ReplayBuild> ResolveAsync(
        OnlineMatch match, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsOriginal(match.GameVersionKind))
        {
            var build = await TryResolveChannelAsync(match, IsPrerelease(match.GameVersionKind),
                progress, ct, cloneIfMissing: true);
            return build ?? throw new InvalidOperationException(
                $"No {match.GameVersionKind} build {match.GameVersion} in the repositories. " +
                "It may be from a build that is no longer published.");
        }

        // Original client: no channel recorded. Try the ordinary channels
        // first — their repos share most of the original history, so this
        // usually costs nothing extra — then the original repository itself.
        var tried = await TryResolveChannelAsync(match, prerelease: false, progress, ct, cloneIfMissing: false);
        tried ??= await TryResolveChannelAsync(match, prerelease: true, progress, ct, cloneIfMissing: false);
        tried ??= await ResolveOriginalAsync(match, progress, ct);
        return tried ?? throw new InvalidOperationException(
            $"No published build {match.GameVersion}. " +
            "It may be from a build that is no longer published.");
    }

    /// <summary>Resolve a Release or Prerelease match against that channel's
    /// fork and upstream remotes in the channel checkout. Returns null when the
    /// commit is not in those repositories (or the checkout does not exist and
    /// <paramref name="cloneIfMissing"/> is false) so callers can fall back to
    /// other sources.</summary>
    private async Task<ReplayBuild?> TryResolveChannelAsync(
        OnlineMatch match, bool prerelease, IProgress<string>? progress, CancellationToken ct,
        bool cloneIfMissing)
    {
        var fork = ForkRepo(prerelease);
        var upstream = UpstreamRepo(prerelease);
        if (fork is null && upstream is null)
            return null;
        var primary = fork ?? upstream
                      ?? throw new InvalidOperationException("No game repository configured for this replay.");

        // One working tree per channel, like CosmoWatch: watching switches the
        // channel checkout to the replay's commit (detached - no branch moves),
        // and Play on the Play page puts the tracked branch back.
        var dirName = prerelease ? "Prerelease" : "Release";
        var dir = Path.Combine(_settings.RootPath, dirName);
        if (!Repository.IsValid(dir))
        {
            if (!cloneIfMissing)
                return null;
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            progress?.Report($"Preparing the {dirName} builds (first use downloads the repository)…");
            new RepoManager(dir, primary.Url).Clone(progress);
        }

        using var repo = new RepoManager(dir, primary.Url);
        var forkRemote = fork is null ? null : repo.EnsureRemote(fork.Url);
        var upstreamRemote = upstream is null ? null : repo.EnsureRemote(upstream.Url);

        TryFetch(repo, forkRemote, progress);
        if (upstreamRemote is not null && upstreamRemote != forkRemote)
            TryFetch(repo, upstreamRemote, progress);

        var full = repo.ResolveCommit(match.GameVersion);
        if (full is null)
            return null;

        var plus = forkRemote is not null && repo.IsInRemote(full, forkRemote);
        var chosen = plus ? fork! : upstream!;
        progress?.Report(
            $"{(plus ? "Cosmonarchy Plus" : "Cosmonarchy OG")} build {full[..8]} ({match.VersionLabel})");
        return new ReplayBuild(
            plus ? ReplayChannel.Plus : ReplayChannel.Original,
            chosen,
            dirName,
            full);
    }

    /// <summary>Resolve a version recorded without a channel against every
    /// published source, the way Fraud Launcher 2's backdate list can reach
    /// them. The checkout lives in its own <c>Original</c> directory and never
    /// disturbs the Play installs. Returns null when no source has the commit.</summary>
    private async Task<ReplayBuild?> ResolveOriginalAsync(
        OnlineMatch match, IProgress<string>? progress, CancellationToken ct)
    {
        var primary = OriginalRepo()
                      ?? throw new InvalidOperationException("No original repository configured for this replay.");
        var forks = new[] { ForkRepo(false), ForkRepo(true) }
            .Where(r => r is not null)
            .Cast<RepoEntry>()
            .ToList();
        var fallbacks = new[] { UpstreamRepo(false), UpstreamRepo(true), OldReleaseRepo(), NightlyRepo() }
            .Where(r => r is not null)
            .Cast<RepoEntry>()
            .ToList();

        var dir = Path.Combine(_settings.RootPath, "Original");
        using var repo = new RepoManager(dir, primary.Url);
        if (!repo.IsInstalled())
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            progress?.Report("Preparing the original builds (first use downloads the repository)…");
            repo.Clone(progress);
        }

        // Fork remotes are registered up front so the maintainer's own games
        // are still labelled Plus; every other source is upstream.
        var forkRemotes = forks.ToDictionary(f => f, f => repo.EnsureRemote(f.Url));

        // The clone itself carries the original history (including the merged
        // Release and Prerelease histories since 2026-09-25), so the common
        // case resolves without fetching anything.
        var already = repo.ResolveCommit(match.GameVersion);
        if (already is not null)
            return OriginalBuild(match, progress, repo, forks, forkRemotes, primary, already);

        // Each additional source only transfers what it adds: the histories
        // share most of their commits.
        foreach (var candidate in forks.Concat(fallbacks).Append(primary))
        {
            var remote = forkRemotes.TryGetValue(candidate, out var forkRemote)
                ? forkRemote
                : repo.EnsureRemote(candidate.Url);
            TryFetch(repo, remote, progress);
            var full = repo.ResolveCommit(match.GameVersion);
            if (full is null)
                continue;
            return OriginalBuild(match, progress, repo, forks, forkRemotes, candidate, full);
        }
        return null;
    }

    private static ReplayBuild OriginalBuild(
        OnlineMatch match, IProgress<string>? progress, RepoManager repo,
        IReadOnlyList<RepoEntry> forks, IReadOnlyDictionary<RepoEntry, string> forkRemotes,
        RepoEntry fallbackRepo, string full)
    {
        var plusRepo = forks.FirstOrDefault(f => repo.IsInRemote(full, forkRemotes[f]));
        var plus = plusRepo is not null;
        var chosen = plusRepo ?? fallbackRepo;
        progress?.Report(
            $"{(plus ? "Cosmonarchy Plus" : "Cosmonarchy OG")} build {full[..8]} ({match.VersionLabel})");
        return new ReplayBuild(
            plus ? ReplayChannel.Plus : ReplayChannel.Original,
            chosen,
            "Original",
            full);
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
