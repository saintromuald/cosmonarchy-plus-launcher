using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>The repositories the launcher knows about, grouped by the
/// variant family they belong to.
///
/// <para><b>Original CM</b> is the vanilla game: the upstream stable
/// (release) and prerelease channels, plus the pre-split and 2023 archives
/// that the launcher can backdate to. <b>Plus</b> is the full product — the
/// maintainer's Plus game builds (the mirror's <c>plus</c> branch), the Plus
/// gameplay features, and Plus Netcode (its online capability).
/// <b>Original CM Netcode</b> arms the Plus Netcode plugin on the vanilla
/// builds, so those are offered there too.</para>
///
/// <para>Upstream's release channel is the stable build: there is no
/// separate <c>cosmonarchy-bw-stable</c> repository. A Stable entry is only
/// listed when such a source exists, so the list never offers a URL that
/// cannot be cloned.</para></summary>
public static class RepoCatalog
{
    public const string DefaultGameRepoId = "release";

    /// <summary>The catalog id of the netcode (rollback) plugin repository.</summary>
    public const string NetcodePluginId = "rollback";

    /// <summary>Official entries, grouped by variant family. The maintainer's
    /// Plus builds come from the GitHub mirrors; upstream GitLab repositories
    /// supply the vanilla (Original CM) channels and the archives.</summary>
    public static IReadOnlyList<RepoEntry> Official { get; } =
    [
        // ---- Original CM: the vanilla game -----------------------------
        new(
            "release-upstream",
            "Original CM — Release (Upstream)",
            "https://gitlab.com/the-no-frauds-club/cosmonarchy-bw-release.git",
            RepoKind.Game,
            BuildDir: "Release",
            Variant: Variant.Original
        ),
        new(
            "prerelease",
            "Original CM — Prerelease (Upstream)",
            "https://gitlab.com/the-no-frauds-club/cosmonarchy-bw-prerelease.git",
            RepoKind.Game,
            BuildDir: "Prerelease",
            Variant: Variant.Original
        ),
        new(
            "original",
            "Original CM — Pre-split Archive",
            "https://gitlab.com/the-no-frauds-club/cosmonarchy.git",
            RepoKind.Game,
            BuildDir: "Original",
            Branch: "master",
            Variant: Variant.Original
        ),
        new(
            "old-release",
            "Original CM — 2023 Archive",
            "https://gitlab.com/the-no-frauds-club/release/Cosmonarchy-old-1.git",
            RepoKind.Game,
            BuildDir: "Original",
            Branch: "master",
            Variant: Variant.Original
        ),
        new(
            "nightly",
            "Original CM — 2023 Nightly",
            "https://gitlab.com/Pr0nogo3/specialbus.git",
            RepoKind.Game,
            BuildDir: "Original",
            Variant: Variant.Original
        ),

        // ---- Plus: the full product (gameplay + online) ----------------
        new(
            "release",
            "Plus — Release",
            "https://github.com/saintromuald/cosmonarchy-bw-release.git",
            RepoKind.Game,
            BuildDir: "Release",
            // The mirror's `main` is the September snapshot; every build since
            // (gptp sync, plugins, lobby templates) lives on `plus`.
            Branch: "plus",
            Variant: Variant.Plus
        ),
        new(
            "prerelease-mine",
            "Plus — Prerelease",
            "https://github.com/saintromuald/cosmonarchy-bw-prerelease.git",
            RepoKind.Game,
            BuildDir: "Prerelease",
            Branch: "plus",
            Variant: Variant.Plus
        ),
    ];

    /// <summary>Community mirror entries, hidden unless requested. They are
    /// vanilla (Original CM) builds.</summary>
    public static IReadOnlyList<RepoEntry> Unofficial { get; } =
    [
        new(
            "luci-release",
            "Original CM — Release (Luci mirror)",
            "https://gitea.aromavir.us/lucifirius/cosmonarchy-bw-release.git",
            RepoKind.Game,
            BuildDir: "Release",
            Unofficial: true,
            Variant: Variant.Original
        ),
        new(
            "luci-prerelease",
            "Original CM — Prerelease (Luci mirror)",
            "https://gitea.aromavir.us/lucifirius/cosmonarchy-bw-prerelease.git",
            RepoKind.Game,
            BuildDir: "Prerelease",
            Unofficial: true,
            Variant: Variant.Original
        ),
    ];

    /// <summary>Map data used by every game entry.</summary>
    public static RepoEntry Maps { get; } = new(
        "cmbw-root",
        "CMBW-Root Maps",
        "https://gitlab.com/the-no-frauds-club/cmbw-root.git",
        RepoKind.Maps
    );

    /// <summary>Plugin repositories: the Plus gameplay features and Plus
    /// Netcode (the rollback online plugin). Staged as zz-cosmo-plus.qdp /
    /// zz-cosmo-rollback.qdp. Neither is a game version; they are shared
    /// plumbing applied automatically when a build launches.</summary>
    public static IReadOnlyList<RepoEntry> Plugins { get; } =
    [
        new(
            "plus",
            "Plus features (auto-managed)",
            "https://github.com/saintromuald/cosmonarchy-plus.git",
            RepoKind.Plugin,
            BuildDir: "zz-cosmo-plus.qdp",
            Variant: Variant.Plus
        ),
        new(
            "rollback",
            "Plus Netcode",
            "https://github.com/saintromuald/cosmonarchy-rollback.git",
            RepoKind.Plugin,
            BuildDir: "zz-cosmo-rollback.qdp",
            // The repository's default branch; `main` does not exist.
            Branch: "cosmonarchy-plus",
            Variant: Variant.Netcode
        ),
    ];

    /// <summary>Game repositories that belong to <paramref name="variant"/>.
    /// Original CM Netcode has no builds of its own: it arms the Plus Netcode
    /// plugin on the vanilla (Original CM) builds, so those are offered
    /// there too.</summary>
    public static IEnumerable<RepoEntry> GameReposFor(Variant variant, bool includeUnofficial)
    {
        var family = variant == Variant.Netcode ? Variant.Original : variant;
        foreach (var repo in Official)
        {
            if (repo.Kind == RepoKind.Game && repo.Variant == family)
                yield return repo;
        }
        if (includeUnofficial && family == Variant.Original)
        {
            foreach (var repo in Unofficial)
                yield return repo;
        }
    }

    /// <summary>The plugins a variant offers for install. Plus lists its
    /// gameplay features and its netcode (online is part of Plus); Original
    /// CM Netcode lists the netcode plugin it arms.</summary>
    public static IEnumerable<RepoEntry> PluginsFor(Variant variant)
    {
        foreach (var plugin in Plugins)
        {
            if (plugin.Variant == variant
                || (variant == Variant.Plus && plugin.Id == NetcodePluginId))
                yield return plugin;
        }
    }

    /// <summary>Everything installable while the given variant is selected:
    /// its game builds first, then its plugins.</summary>
    public static IReadOnlyList<RepoEntry> ForVariant(Variant variant, bool includeUnofficial) =>
        GameReposFor(variant, includeUnofficial).Concat(PluginsFor(variant)).ToList();

    public static RepoEntry? Find(string id) =>
        Official.Concat(Unofficial).Append(Maps).Concat(Plugins)
               .FirstOrDefault(r => r.Id == id);
}
