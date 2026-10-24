using Octokit;

namespace CosmonarchyPlusLauncher.Core.Services;

public sealed record UpdateInfo(string Tag, string Name, string Url, string Notes, bool IsNewer);

/// <summary>Self-update check against this project's GitHub Releases.</summary>
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
            return new UpdateInfo(
                release.TagName,
                release.Name ?? release.TagName,
                release.HtmlUrl,
                release.Body ?? "",
                CompareVersions(tag, currentVersion) > 0);
        }
        catch
        {
            return null; // offline or rate-limited: not an error state
        }
    }

    private static int CompareVersions(string a, string b)
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
