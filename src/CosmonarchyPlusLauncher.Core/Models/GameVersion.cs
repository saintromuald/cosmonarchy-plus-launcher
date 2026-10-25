using System.Text.Json.Serialization;
using CosmonarchyPlusLauncher.Core.Services;

namespace CosmonarchyPlusLauncher.Core.Models;

/// <summary>One installed copy of the game build. Versions share the
/// StarCraft base, the maps checkout, and the patches under the install
/// root; each has its own game-repo checkout directory and either tracks
/// the repo's branch (auto-updates to the newest build) or is pinned to a
/// specific commit (frozen until explicitly updated or removed).</summary>
public sealed class GameVersion
{
    /// <summary>Stable id used in settings (SelectedVersionId) and in the
    /// umu GAMEID. New installs also use it as the checkout folder name
    /// under versions/.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name shown in the versions list.</summary>
    public string Name { get; set; } = "";

    /// <summary>RepoCatalog id of the repository this version came from.</summary>
    public string RepoId { get; set; } = "";

    /// <summary>Checkout directory relative to the install root
    /// (e.g. "Release" for a legacy install, "versions/foo" for new ones).</summary>
    public string Dir { get; set; } = "";

    /// <summary>When set, the version stays frozen at this commit and is
    /// never fetched or moved on play/update. When null, it tracks the
    /// repo's branch and updates to the newest build.</summary>
    public string? PinnedSha { get; set; }

    /// <summary>Commit currently checked out (informational).</summary>
    public string? InstalledSha { get; set; }

    /// <summary>The variant this install belongs to (the Original / Plus /
    /// Netcode filter on the Play page). Null on records written before the
    /// variants existed: those fall back to the repository's own family.</summary>
    public Variant? Variant { get; set; }

    public bool TracksBranch => string.IsNullOrEmpty(PinnedSha);

    [JsonIgnore]
    public RepoEntry? Repo => string.IsNullOrEmpty(RepoId) ? null : RepoCatalog.Find(RepoId);

    /// <summary>The variant used for filtering and for plugin staging.
    /// Legacy records inherit their repo's family so existing installs land
    /// in the right group without being rewritten.</summary>
    [JsonIgnore]
    public Variant EffectiveVariant =>
        Variant ?? Repo?.Variant ?? global::CosmonarchyPlusLauncher.Core.Models.Variant.Original;

    [JsonIgnore]
    public string VariantLabel => EffectiveVariant.ToString();

    [JsonIgnore]
    public string Badge => EffectiveVariant.ToString().ToUpperInvariant();

    /// <summary>One-line description for the versions list.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var repo = Repo?.Name ?? RepoId;
            var state = TracksBranch
                ? $"tracks {Repo?.Branch ?? "main"} (auto-updates)"
                : $"pinned {Short(PinnedSha!)}";
            var at = string.IsNullOrEmpty(InstalledSha)
                ? " · not downloaded yet"
                : $" · now {Short(InstalledSha)}";
            return $"{repo} · {state}{at}";
        }
    }

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "?" : sha[..Math.Min(8, sha.Length)];
}
