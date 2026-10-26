using System.Text.Json.Serialization;
using CosmonarchyPlusLauncher.Core.Services;

namespace CosmonarchyPlusLauncher.Core.Models;

/// <summary>An installed plugin build (a .qdp staged outside the game tree,
/// in the variants directory). The Plus and Netcode families install these:
/// the game checkout supplies the engine, the plugin supplies the variant.</summary>
public sealed class PluginVersion
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>RepoCatalog id of the plugin repository.</summary>
    public string RepoId { get; set; } = "";

    public Variant Variant { get; set; } = Variant.Plus;

    /// <summary>Release tag or commit the plugin was installed from.</summary>
    public string Version { get; set; } = "";

    public string? InstalledSha { get; set; }

    /// <summary>Checkout directory for repo-based plugin installs, relative
    /// to the install root. Null for release-asset installs (the Plus
    /// plugin ships as a GitHub release zip).</summary>
    public string? CheckoutDir { get; set; }

    public List<PluginFile> Files { get; set; } = [];

    [JsonIgnore]
    public RepoEntry? Repo => string.IsNullOrEmpty(RepoId) ? null : RepoCatalog.Find(RepoId);

    [JsonIgnore]
    public string VariantLabel => Variant.Label();

    [JsonIgnore]
    public string Badge => Variant.BadgeText();

    /// <summary>One-line description for the installed list.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var repo = Repo?.Name ?? RepoId;
            var version = string.IsNullOrEmpty(Version) ? "latest" : Version;
            var files = Files.Count == 0
                ? "no files recorded"
                : string.Join(", ", Files.Select(f => f.Name));
            return $"{repo} · plugin {version} · {files}";
        }
    }
}

/// <summary>A .qdp file a plugin install put in the variants directory. The
/// hash lets Remove delete only the file this record actually installed, so
/// a hand-copied build is never touched.</summary>
public sealed class PluginFile
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}
