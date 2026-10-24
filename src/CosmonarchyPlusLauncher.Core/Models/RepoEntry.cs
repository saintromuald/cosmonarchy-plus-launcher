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
    bool Unofficial = false
)
{
    public override string ToString() => Name;
}
