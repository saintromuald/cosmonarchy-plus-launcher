using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Settings;
using LibGit2Sharp;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Tracks the installed game versions and the shared base, and
/// orchestrates install / update / play / remove across them. The pipeline
/// work itself lives in <see cref="InstallService"/>; this class owns the
/// version records in settings and the mapping between records and checkout
/// directories.
///
/// Layout: all versions live inside the install root. Legacy installs keep
/// their historical directories (Release, Prerelease); new installs go into
/// versions/&lt;id&gt;/. The StarCraft base, patches, CrownLink, and maps are
/// shared by every version.</summary>
public sealed class VersionManager
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<RepoEntry>? _known;

    public VersionManager(AppSettings settings, IReadOnlyList<RepoEntry>? known = null)
    {
        _settings = settings;
        _known = known;
    }

    public IReadOnlyList<GameVersion> Versions => _settings.Versions;

    /// <summary>Game versions belonging to a variant family. Legacy records
    /// without a stored variant use their repository's family.</summary>
    public IReadOnlyList<GameVersion> VersionsFor(Variant variant) =>
        Versions.Where(v => v.EffectiveVariant == variant).ToList();

    public IReadOnlyList<PluginVersion> PluginsFor(Variant variant) =>
        _settings.Plugins.Where(p => p.Variant == variant).ToList();

    /// <summary>The version the play/update/delete actions act on: the
    /// persisted selection, or the first installed version as a fallback.</summary>
    public GameVersion? Selected =>
        Find(_settings.SelectedVersionId) ?? Versions.FirstOrDefault();

    public GameVersion? Find(string? id) =>
        id is null ? null : Versions.FirstOrDefault(v => v.Id == id);

    public void Select(string? id)
    {
        if (id is null || Find(id) is null)
            return;
        _settings.SelectedVersionId = id;
        _settings.Save();
    }

    /// <summary>Populate the version list from checkouts that already exist
    /// under the install root: a legacy Release/Prerelease install or
    /// leftover versions/* directories. Runs only when no versions are
    /// recorded, so a re-scan never duplicates or rewrites the user's list.
    /// Returns the number of versions adopted.</summary>
    public int AdoptExistingInstallations(IEnumerable<RepoEntry>? known = null)
    {
        if (Versions.Count > 0)
            return 0;

        var gameRepos = (known ?? _known ?? KnownGameRepos()).ToList();
        var root = _settings.RootPath;
        if (!Directory.Exists(root))
            return 0;

        var candidates = new List<string>();
        foreach (var legacy in new[] { "Release", "Prerelease" })
        {
            var dir = Path.Combine(root, legacy);
            if (Directory.Exists(dir))
                candidates.Add(legacy);
        }
        var versionsRoot = Path.Combine(root, "versions");
        if (Directory.Exists(versionsRoot))
        {
            foreach (var sub in Directory.EnumerateDirectories(versionsRoot).OrderBy(d => d))
                candidates.Add(Path.GetRelativePath(root, sub).Replace('\\', '/'));
        }

        var adopted = 0;
        foreach (var dir in candidates)
        {
            var repo = MatchRepo(Path.Combine(root, dir), gameRepos);
            if (repo is null)
                continue; // not a game checkout we recognize; leave it alone

            var version = new GameVersion
            {
                Id = dir is "Release" or "Prerelease"
                    ? dir.ToLowerInvariant()
                    : Path.GetFileName(dir),
                Name = dir is "Release" or "Prerelease"
                    ? repo.Name
                    : Prettify(Path.GetFileName(dir)),
                RepoId = repo.Id,
                Dir = dir,
                Variant = repo.Variant,
            };
            EnsureUniqueId(version, Versions);
            EnsureUniqueName(version, Versions);
            RecordHead(version, repo);
            _settings.Versions.Add(version);
            adopted++;
        }

        if (adopted > 0)
        {
            _settings.SelectedVersionId ??= _settings.Versions[0].Id;
            _settings.Save();
        }
        return adopted;
    }

    /// <summary>Build the record for a new install without touching the
    /// disk: a unique id and a checkout dir under versions/. The name
    /// defaults to "{repo name} — Latest" or "{repo name} — {sha8}" and is
    /// de-duplicated when the same repo is installed again.</summary>
    public GameVersion CreateNew(RepoEntry repo, string? pinnedSha, string? requestedName,
        Variant? variant = null)
    {
        var family = variant ?? repo.Variant;
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? DefaultName(repo, pinnedSha, family)
            : requestedName.Trim();
        var version = new GameVersion
        {
            Id = Slug(name) is { Length: > 0 } slug ? slug : repo.Id,
            Name = name,
            RepoId = repo.Id,
            PinnedSha = pinnedSha,
            Variant = family,
        };
        EnsureUniqueId(version, Versions);
        EnsureUniqueName(version, Versions);
        version.Dir = Path.Combine("versions", version.Id).Replace('\\', '/');
        return version;
    }

    /// <summary>Install a brand-new version: record it first (so it appears
    /// in the installed list immediately, with "not downloaded yet"), then
    /// ensure the shared base, clone (or fetch and pin) the game repo into
    /// its own directory, and select it.
    ///
    /// A failed clone leaves the record in place: the failure is reported to
    /// the caller, and pressing Update retries instead of losing the entry.</summary>
    public async Task<GameVersion> InstallAsync(
        RepoEntry repo,
        string? pinnedSha,
        string? requestedName,
        IProgress<string>? progress = null,
        string? mapsUrl = null,
        CancellationToken ct = default,
        Variant? variant = null)
    {
        var version = CreateNew(repo, pinnedSha, requestedName, variant);
        _settings.Versions.Add(version);
        _settings.SelectedVersionId = version.Id;
        _settings.Save();
        DiagnosticsLog.Info($"install start: {version.Name} ({repo.Id} → {version.Dir})");

        try
        {
            var service = new InstallService(_settings, repo, version, mapsUrl);
            await service.InstallAsync(progress, ct);
            RecordHead(version, repo);
            DiagnosticsLog.Info($"install done: {version.Name} @ {version.InstalledSha}");
            return version;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error($"install failed: {version.Name}", ex);
            throw;
        }
        finally
        {
            _settings.Save();
        }
    }

    /// <summary>Update one version: tracked versions fetch and move to the
    /// newest branch tip; pinned versions re-reset to their pin (recovering
    /// from any drift). Checkouts with local changes are left alone.</summary>
    public async Task UpdateAsync(
        GameVersion version,
        IProgress<string>? progress = null,
        string? mapsUrl = null,
        CancellationToken ct = default)
    {
        var repo = RequireRepo(version);
        var service = new InstallService(_settings, repo, version, mapsUrl);
        await service.InstallAsync(progress, ct);
        RecordHead(version, repo);
        _settings.Save();
    }

    public async Task PlayAsync(
        GameVersion version,
        IProgress<string>? progress = null,
        string? mapsUrl = null,
        CancellationToken ct = default)
    {
        var repo = RequireRepo(version);
        var service = new InstallService(_settings, repo, version, mapsUrl);
        await service.PlayAsync(progress, ct);
        RecordHead(version, repo);
        _settings.Save();
    }

    public async Task PlayReplayAsync(
        GameVersion version,
        string replayPath,
        IProgress<string>? progress = null,
        string? mapsUrl = null,
        CancellationToken ct = default)
    {
        var repo = RequireRepo(version);
        var service = new InstallService(_settings, repo, version, mapsUrl);
        await service.PlayReplayAsync(replayPath, progress, ct);
        RecordHead(version, repo);
        _settings.Save();
    }

    /// <summary>Remove the version record and its checkout directory. The
    /// directory is only deleted when it is strictly inside the install
    /// root, never the root itself, so a misconfigured record cannot wipe
    /// anything else.</summary>
    public void Remove(GameVersion version)
    {
        var dir = Path.Combine(_settings.RootPath, version.Dir);
        var root = Path.GetFullPath(_settings.RootPath).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.Exists(dir) && full != root
            && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            Directory.Delete(dir, recursive: true);
        }

        _settings.Versions.Remove(version);
        if (_settings.SelectedVersionId == version.Id)
            _settings.SelectedVersionId = _settings.Versions.FirstOrDefault()?.Id;
        _settings.Save();
    }

    /// <summary>Where a repo's commit log can be read when installing a new
    /// version: the legacy build dir if it holds this repo, else the first
    /// version checkout of that repo. Null when the repo has no checkout
    /// anywhere yet (only "Latest" is offered then).</summary>
    public string? CanonicalDir(RepoEntry repo)
    {
        var root = _settings.RootPath;
        if (!Directory.Exists(root))
            return null;

        var candidates = new List<string>();
        var legacy = Path.Combine(root, repo.BuildDir ?? "Release");
        if (Directory.Exists(legacy))
            candidates.Add(legacy);
        foreach (var version in Versions)
        {
            if (version.RepoId != repo.Id)
                continue;
            var dir = Path.Combine(root, version.Dir);
            if (Directory.Exists(dir))
                candidates.Add(dir);
        }

        foreach (var dir in candidates)
        {
            try
            {
                if (new RepoManager(dir, repo.Url).IsInstalled())
                    return dir;
            }
            catch (LibGit2SharpException)
            {
            }
        }
        return null;
    }

    public static string DefaultName(RepoEntry repo, string? pinnedSha, Variant? variant = null)
    {
        var family = variant ?? repo.Variant;
        // Only spell the variant out when it differs from the repository's own
        // family (a Netcode install of a Plus build), so ordinary names stay
        // short while the list can still tell the two apart.
        var suffix = family == repo.Variant ? "" : $" — {family}";
        return pinnedSha is null
            ? $"{repo.Name}{suffix} — Latest"
            : $"{repo.Name}{suffix} — {pinnedSha[..Math.Min(8, pinnedSha.Length)]}";
    }

    /// <summary>Two installs of the same repo used to produce identical
    /// display names, which made the list look like the new install never
    /// appeared. Rename duplicates to " (2)", " (3)", … once, without moving
    /// any checkout. Returns true when a record changed.</summary>
    public bool NormalizeDuplicateNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var version in Versions)
        {
            if (seen.Add(version.Name))
                continue;
            var baseName = version.Name;
            for (var n = 2; ; n++)
            {
                var candidate = $"{baseName} ({n})";
                if (seen.Add(candidate))
                {
                    version.Name = candidate;
                    changed = true;
                    break;
                }
            }
        }
        return changed;
    }

    private RepoEntry RequireRepo(GameVersion version) =>
        ResolveRepo(version.RepoId)
        ?? throw new InvalidOperationException(
            $"Unknown repository '{version.RepoId}' for {version.Name}.");

    /// <summary>Resolve a repo id: injected known entries first (tests,
    /// smoke runs), then the real catalog.</summary>
    private RepoEntry? ResolveRepo(string id) =>
        _known?.FirstOrDefault(r => r.Id == id) ?? RepoCatalog.Find(id);

    private IEnumerable<RepoEntry> KnownGameRepos() =>
        RepoCatalog.Official.Concat(RepoCatalog.Unofficial)
            .Where(r => r.Kind == RepoKind.Game);

    private static RepoEntry? MatchRepo(string dir, IEnumerable<RepoEntry> gameRepos)
    {
        if (!Repository.IsValid(dir))
            return null;

        var matches = new List<RepoEntry>();
        foreach (var entry in gameRepos)
        {
            try
            {
                using var manager = new RepoManager(dir, entry.Url);
                if (manager.IsInstalled())
                    matches.Add(entry);
            }
            catch (LibGit2SharpException)
            {
            }
        }
        if (matches.Count <= 1)
            return matches.FirstOrDefault();

        // One checkout often carries several matching remotes (the upstream
        // origin plus the fork). Prefer the repository whose branch is
        // checked out — the Release checkout on `plus` is a Plus build, not
        // the upstream release — then the clone's origin remote.
        using var repo = new Repository(dir);
        var branch = repo.Info.IsHeadDetached ? null : repo.Head.FriendlyName;
        var byBranch = branch is null
            ? new List<RepoEntry>()
            : matches.Where(m => string.Equals(m.Branch, branch, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = byBranch.Count > 0 ? byBranch : matches;

        var origin = repo.Network.Remotes["origin"]?.Url;
        if (origin is not null)
        {
            var byOrigin = pool.Where(m =>
                string.Equals(NormalizeUrl(m.Url), NormalizeUrl(origin), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byOrigin.Count > 0)
                return byOrigin[0];
        }
        return pool[0];
    }

    private static string NormalizeUrl(string url) => url.TrimEnd('/', '\\');

    private void RecordHead(GameVersion version, RepoEntry? repo = null)
    {
        // The repo is passed by callers that already resolved it; fall back
        // to the catalog for records read from disk.
        repo ??= version.Repo;
        if (repo is null)
            return;
        try
        {
            using var manager = new RepoManager(Path.Combine(_settings.RootPath, version.Dir), repo.Url);
            if (!manager.IsInstalled())
                return;
            var head = manager.NewestCommit();
            if (head is not null)
                version.InstalledSha = head.Sha;
        }
        catch (Exception)
        {
            // informational only; never fail an install over the label
        }
    }

    private static void EnsureUniqueId(GameVersion version, IEnumerable<GameVersion> existing)
    {
        var ids = existing.Select(v => v.Id).ToHashSet();
        if (!ids.Contains(version.Id))
            return;
        for (var n = 2; ; n++)
        {
            var candidate = $"{version.Id}-{n}";
            if (!ids.Contains(candidate))
            {
                // Id only: the directory mapping is the caller's concern
                // (adopted legacy checkouts keep their own directories).
                version.Id = candidate;
                return;
            }
        }
    }

    /// <summary>Two installs of the same repo used to produce identical
    /// display names, which made the list look like the new install never
    /// appeared. Suffix repeats with " (2)", " (3)", ….</summary>
    private static void EnsureUniqueName(GameVersion version, IEnumerable<GameVersion> existing)
    {
        var names = existing.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(version.Name))
            return;
        var baseName = version.Name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} ({n})";
            if (!names.Contains(candidate))
            {
                version.Name = candidate;
                return;
            }
        }
    }

    private static string Prettify(string slug)
    {
        var words = slug.Split('-').Where(s => s.Length > 0)
            .Select(w => string.Concat(w[..1].ToUpperInvariant(), w[1..]));
        var joined = string.Join(' ', words);
        return joined.Length > 0 ? joined : slug;
    }

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var result = new List<char>();
        foreach (var c in chars)
        {
            if (c == '-' && (result.Count == 0 || result[^1] == '-'))
                continue;
            result.Add(c);
        }
        return new string(result.ToArray()).Trim('-');
    }
}
