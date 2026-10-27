using Octokit;

namespace CosmonarchyPlusLauncher.Core.Services;

public sealed record UpdateInfo(
    string Tag,
    string Name,
    string Url,
    string Notes,
    bool IsNewer,
    string? AssetUrl,
    string? AssetName);

/// <summary>Both update channels at once: the latest stable release and the
/// latest prerelease (so the UI can show both numbers and say whether the
/// running build is the newest).</summary>
public sealed record LauncherVersions(string? LatestReleaseTag, string? LatestPrereleaseTag);

/// <summary>Self-update check against this project's GitHub Releases.
/// The CI attaches per-platform assets to every tagged release:
/// <c>CosmonarchyPlusLauncher-linux-x64.tar.gz</c> (self-contained directory
/// publish) and <c>CosmonarchyPlusLauncher-win-x64.zip</c> (single-file exe),
/// plus an optional AppImage.</summary>
public sealed class UpdateService
{
    public const string Owner = "saintromuald";
    public const string Repo = "cosmonarchy-plus-launcher";

    private static readonly GitHubClient Client = new(new ProductHeaderValue("CosmonarchyPlusLauncher"));

    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var release = await Client.Repository.Release.GetLatest(Owner, Repo);
            if (release is null)
                return null;
            var tag = release.TagName.TrimStart('v', 'V');
            var asset = PickAsset(release.Assets, OperatingSystem.IsWindows());
            return new UpdateInfo(
                release.TagName,
                release.Name ?? release.TagName,
                release.HtmlUrl,
                release.Body ?? "",
                CompareVersions(tag, currentVersion) > 0,
                asset?.BrowserDownloadUrl,
                asset?.Name);
        }
        catch
        {
            return null; // offline or rate-limited: not an error state
        }
    }

    /// <summary>The latest stable and prerelease tags (v-less), or null when
    /// GitHub could not be reached. The stable number is what "are we on the
    /// latest version" compares against; the prerelease number is shown for
    /// people who want to ride ahead of stable.</summary>
    public async Task<LauncherVersions?> GetVersionsAsync(CancellationToken ct = default)
    {
        try
        {
            var releases = await Client.Repository.Release.GetAll(Owner, Repo);
            string? stable = null;
            string? pre = null;
            foreach (var release in releases)
            {
                if (release.Draft)
                    continue;
                var tag = release.TagName.TrimStart('v', 'V');
                if (release.Prerelease)
                {
                    if (pre is null || CompareVersions(tag, pre) > 0)
                        pre = tag;
                }
                else
                {
                    if (stable is null || CompareVersions(tag, stable) > 0)
                        stable = tag;
                }
            }
            return new LauncherVersions(stable, pre);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The release asset matching this platform, or null when the
    /// release carries none (e.g. an AppImage-only or package-managed setup
    /// must update through its own channel).</summary>
    internal static ReleaseAsset? PickAsset(IReadOnlyList<ReleaseAsset> assets, bool windows) =>
        windows
            ? assets.FirstOrDefault(a => a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
            : assets.FirstOrDefault(a => a.Name.EndsWith("-linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase));

    internal static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) => (v.Split('-')[0]).Split('.')
            .Select(s => int.TryParse(s, out var n) ? n : 0).Concat(Enumerable.Repeat(0, 4)).Take(4).ToArray();
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < 4; i++)
        {
            if (x[i] != y[i])
                return x[i].CompareTo(y[i]);
        }
        return 0;
    }
}
