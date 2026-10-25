namespace CosmonarchyPlusLauncher.Core.Models;

/// <summary>A git repository the launcher can install or update.</summary>
public enum RepoKind
{
    /// <summary>The game itself (cloned into &lt;root&gt;/Release or /Prerelease).</summary>
    Game,

    /// <summary>Map data (cloned into Starcraft/maps/CMBW-Root).</summary>
    Maps,

    /// <summary>A plugin repo that stages a .qdp into the game's plugins/ dir.</summary>
    Plugin,
}

public sealed record RepoEntry(
    string Id,
    string Name,
    string Url,
    RepoKind Kind,
    string? CmbwRootUrl = null,
    string? BuildDir = null,
    bool Unofficial = false,
    string Branch = "main",
    Variant Variant = global::CosmonarchyPlusLauncher.Core.Models.Variant.Original
)
{
    public override string ToString() => Name;

    /// <summary>Short badge text shown next to the name ("ORIGINAL",
    /// "PLUS", "NETCODE").</summary>
    public string Badge => Variant.ToString().ToUpperInvariant();

    /// <summary>Older name kept for the plugin staging contract: the .qdp
    /// file a plugin repo installs.</summary>
    public string? PluginFileName => Kind == RepoKind.Plugin ? BuildDir : null;
}
