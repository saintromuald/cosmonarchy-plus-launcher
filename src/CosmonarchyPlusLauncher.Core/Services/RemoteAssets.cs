using CosmonarchyPlusLauncher.Core.Models;
using Octokit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Remote assets the install pipeline downloads.</summary>
public static class RemoteAssets
{
    public const string StarcraftZip = "http://fraudsclub.com/files/starcraft.zip";
    public const string VsRedist = "https://aka.ms/vs/17/release/vc_redist.x86.exe";
    public const string PatchManifest = "https://cosmonarchy.com/files/patches/patches.yml";

    /// <summary>Upstream CrownLink SNP (the network plugin the game expects).</summary>
    public const string CrownLinkOwner = "impromptu1583";
    public const string CrownLinkRepo = "SNPJuice";
}

/// <summary>Downloads the CrownLink SNP from its GitHub release, caching by
/// asset id + size so it is fetched once per version.</summary>
public sealed class CrownLinkService
{
    private static readonly GitHubClient Client = new(new ProductHeaderValue("CosmonarchyPlusLauncher"));

    public async Task<(string Url, long Size, int Id)> GetLatestSnpAsync(CancellationToken ct = default)
    {
        var release = await Client.Repository.Release.GetLatest(RemoteAssets.CrownLinkOwner, RemoteAssets.CrownLinkRepo)
                      ?? throw new InvalidOperationException("Could not retrieve the latest CrownLink release.");
        var asset = release.Assets.FirstOrDefault(a =>
                        a.Name.Equals("crownlink.snp", StringComparison.OrdinalIgnoreCase))
                    ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".snp", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("Latest CrownLink release has no .snp asset.");
        return (asset.BrowserDownloadUrl, asset.Size, asset.Id);
    }
}

/// <summary>Loads the remote patch manifest (YAML: id/url/filename entries).</summary>
public sealed class PatchManifestService
{
    private static readonly HttpClient Http = new();

    public async Task<IReadOnlyList<Patch>> LoadAsync(CancellationToken ct = default)
    {
        using var res = await Http.GetAsync(RemoteAssets.PatchManifest, ct);
        res.EnsureSuccessStatusCode();
        var yaml = await res.Content.ReadAsStringAsync(ct);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<List<Patch>>(yaml) ?? [];
    }
}
