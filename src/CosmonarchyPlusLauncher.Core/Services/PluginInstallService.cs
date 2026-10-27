using System.IO.Compression;
using System.Security.Cryptography;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Settings;
using Octokit;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Installs plugin builds into the shared variants directory, so the
/// Plus and Netcode families have something concrete to stage.
///
/// <para><b>Plus</b> ships as a GitHub release: the launcher downloads the
/// newest <c>CosmonarchyPlus-plugins-*.zip</c> asset and extracts its .qdp
/// files (the Plus plugin and the rollback build it is pinned against).
/// <b>Rollback</b> has no release asset and does not commit its .qdp, so the
/// launcher clones the repository to track versions and then copies the
/// plugin from the checkout if it was built there, from the user's local
/// project build, or from an already-staged copy.</para>
///
/// <para>Files are recorded with their SHA-256, so Remove only deletes a file
/// this record actually wrote and never a hand-built copy that replaced it.</para></summary>
public sealed class PluginInstallService
{
    private static readonly GitHubClient GitHub = new(new ProductHeaderValue("CosmonarchyPlusLauncher"));
    private readonly AppSettings _settings;
    private readonly RepoEntry _repo;

    public PluginInstallService(AppSettings settings, RepoEntry repo)
    {
        _settings = settings;
        _repo = repo;
        if (repo.Kind != RepoKind.Plugin)
            throw new ArgumentException($"{repo.Name} is not a plugin repository.", nameof(repo));
    }

    /// <summary>Install (or re-install/update) this plugin. <paramref name="existing"/>
    /// keeps the record id and is updated in place.</summary>
    public async Task<PluginVersion> InstallAsync(
        PluginVersion? existing = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var stage = InstallPaths.StageDir(_settings);
        Directory.CreateDirectory(stage);
        DiagnosticsLog.Info($"plugin install start: {_repo.Id} → {stage}");

        try
        {
            var artifact = _repo.Id is "plus" or "rollback"
                ? await FromGitHubReleaseAsync(stage, progress, ct)
                : await FromRepositoryAsync(stage, progress, ct);

            var version = existing ?? new PluginVersion
            {
                Id = UniqueId(Slug($"{_repo.Name}-{artifact.Version}"), _settings.Plugins),
                RepoId = _repo.Id,
                Variant = _repo.Variant,
            };
            version.RepoId = _repo.Id;
            version.Variant = _repo.Variant;
            version.Name = $"{_repo.Name} — {artifact.Version}";
            version.Version = artifact.Version;
            version.InstalledSha = artifact.Sha;
            version.CheckoutDir = artifact.CheckoutDir;
            version.Files = artifact.Files;

            if (existing is null)
                _settings.Plugins.Add(version);
            _settings.Save();

            foreach (var file in artifact.Files)
                progress?.Report($"Staged {file.Name} ({file.Size / 1024} KB)");
            DiagnosticsLog.Info($"plugin install done: {version.Name} ({artifact.Version})");
            return version;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error($"plugin install failed: {_repo.Id}", ex);
            throw;
        }
    }

    public static void Remove(AppSettings settings, PluginVersion plugin)
    {
        var stage = InstallPaths.StageDir(settings);
        var ownedElsewhere = settings.Plugins
            .Where(p => p.Id != plugin.Id)
            .SelectMany(p => p.Files)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in plugin.Files)
        {
            if (ownedElsewhere.Contains(file.Name))
                continue; // another installed plugin package still needs it
            var path = Path.Combine(stage, file.Name);
            if (File.Exists(path) && Hash(path) == file.Sha256)
                File.Delete(path);
        }

        if (!string.IsNullOrEmpty(plugin.CheckoutDir))
        {
            var root = Path.GetFullPath(settings.RootPath).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(settings.RootPath, plugin.CheckoutDir))
                .TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(full)
                && full != root
                && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                Directory.Delete(full, recursive: true);
            }
        }

        settings.Plugins.Remove(plugin);
        settings.Save();
    }

    private sealed record Artifact(
        string Version,
        string? Sha,
        string? CheckoutDir,
        List<PluginFile> Files);

    /// <summary>Plus: newest published release asset. Stable releases win;
    /// when there are none, the newest prerelease is used.</summary>
    private async Task<Artifact> FromGitHubReleaseAsync(
        string stage, IProgress<string>? progress, CancellationToken ct)
    {
        var (owner, name) = ParseGitHub(_repo.Url);
        Release release;
        try
        {
            release = await GitHub.Repository.Release.GetLatest(owner, name);
        }
        catch (NotFoundException)
        {
            var releases = await GitHub.Repository.Release.GetAll(owner, name);
            release = releases.FirstOrDefault(r => !r.Draft)
                      ?? throw new InvalidOperationException(
                          $"{_repo.Name} has no published release with plugin assets.");
        }

        var asset = release.Assets.FirstOrDefault(a =>
                        a.Name.Contains("plugins", StringComparison.OrdinalIgnoreCase)
                        && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ?? release.Assets.FirstOrDefault(a =>
                        a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"Release {release.TagName} of {_repo.Name} has no plugin zip asset.");

        progress?.Report($"Downloading {asset.Name}…");
        // Download next to the target: the system temp directory may be a
        // small tmpfs (this machine's is), and the plugin zip would fail
        // there even though the install root has room.
        var temp = Path.Combine(stage, $".{asset.Name}.download");
        try
        {
            using (var http = new HttpClient())
                await HttpDownload.ToFileAsync(http, asset.BrowserDownloadUrl, temp, progress,
                    $"Downloading {asset.Name}", ct);

            var files = new List<PluginFile>();
            using var zip = ZipFile.OpenRead(temp);
            foreach (var entry in zip.Entries)
            {
                var fileName = Path.GetFileName(entry.FullName);
                if (!fileName.EndsWith(".qdp", StringComparison.OrdinalIgnoreCase))
                    continue;
                // The Plus gameplay zip historically carried a stale zz-cosmo-rollback.qdp.
                // The netcode plugin is now published from its own repo (release asset), so
                // the Plus install must not plant an outdated copy over a newer one.
                if (fileName.Equals("zz-cosmo-rollback.qdp", StringComparison.OrdinalIgnoreCase)
                    && _repo.Id != "rollback")
                    continue;
                var target = Path.Combine(stage, fileName);
                entry.ExtractToFile(target, overwrite: true);
                files.Add(new PluginFile
                {
                    Name = fileName,
                    Sha256 = Hash(target),
                    Size = new FileInfo(target).Length,
                });
            }
            if (files.Count == 0)
                throw new InvalidDataException($"{asset.Name} contains no .qdp plugin files.");

            return new Artifact(release.TagName, null, null, files);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Other plugin repositories: clone/fetch the repository for version
    /// tracking, then copy the .qdp it contains (or the local build, when the checked
    /// out tree has no built plugin). Only used for repos without a release flow.</summary>
    private async Task<Artifact> FromRepositoryAsync(
        string stage, IProgress<string>? progress, CancellationToken ct)
    {
        var dir = InstallPaths.PluginCheckoutDir(_settings, _repo.Id);
        using var repo = new RepoManager(dir, _repo.Url);
        var branch = string.IsNullOrWhiteSpace(_repo.Branch) ? "main" : _repo.Branch;
        if (repo.IsInstalled())
        {
            var remote = repo.EnsureRemoteName();
            repo.Fetch(progress);
            repo.CheckoutBranch(branch, remote, progress);
        }
        else
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            repo.Clone(progress);
            var remote = repo.EnsureRemoteName();
            repo.CheckoutBranch(branch, remote, progress);
        }

        var head = repo.NewestCommit();
        var sha = head?.Sha;
        progress?.Report(sha is null ? "Locating the plugin build…" : $"Plugin checkout {sha[..8]}");

        var source = PluginSources.Find(_repo.Id, dir)
                     ?? throw new InvalidOperationException(
                         $"No built {_repo.PluginFileName} was found in {dir} or in the local " +
                         $"project. Build the plugin first, or put a copy in {stage}.");

        var target = Path.Combine(stage, Path.GetFileName(_repo.PluginFileName)!);
        File.Copy(source, target, overwrite: true);
        var file = new PluginFile
        {
            Name = Path.GetFileName(target),
            Sha256 = Hash(target),
            Size = new FileInfo(target).Length,
        };

        return new Artifact(
            sha is null ? "latest" : sha[..Math.Min(8, sha.Length)],
            sha,
            Path.GetRelativePath(_settings.RootPath, dir).Replace('\\', '/'),
            [file]);
    }

    private static (string Owner, string Name) ParseGitHub(string url)
    {
        var uri = new Uri(url);
        if (!uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Release downloads need a github.com repository, not {uri.Host}.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2)
            throw new InvalidOperationException($"Cannot read owner/name from {url}.");
        return (parts[0], parts[1].EndsWith(".git") ? parts[1][..^4] : parts[1]);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string UniqueId(string candidate, IEnumerable<PluginVersion> existing)
    {
        var id = string.IsNullOrEmpty(candidate) ? "plugin" : candidate;
        var ids = existing.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!ids.Contains(id))
            return id;
        for (var n = 2; ; n++)
        {
            var next = $"{id}-{n}";
            if (!ids.Contains(next))
                return next;
        }
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

/// <summary>Where a plugin repository keeps its built .qdp, in preference
/// order: the checkout in the install root, this machine's project tree, and
/// a Rust build output as the last resort.</summary>
public static class PluginSources
{
    public static string? Find(string repoId, string? checkoutDir = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();
        if (checkoutDir is not null)
        {
            candidates.Add(Path.Combine(checkoutDir, "dist", "rollback-client", "plugin",
                "zz-cosmo-rollback.qdp"));
            candidates.Add(Path.Combine(checkoutDir, "dist", "netcode-installer", "payload",
                "zz-cosmo-rollback.qdp"));
        }

        if (repoId == "rollback")
        {
            var project = Path.Combine(home, "Projects", "cosmonarchy-rollback");
            candidates.Add(Path.Combine(project, "dist", "rollback-client", "plugin",
                "zz-cosmo-rollback.qdp"));
            foreach (var config in new[] { "release", "debug" })
                candidates.Add(Path.Combine(project, "target", "i686-pc-windows-msvc", config,
                    "cosmo_rollback.dll"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
