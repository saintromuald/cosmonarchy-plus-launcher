using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>The repositories the launcher knows about.
///
/// The first game entry is the user's own GitHub mirror; the upstream GitLab
/// repositories remain available as fallback entries (mirroring the original
/// launcher's official/unofficial split).</summary>
public static class RepoCatalog
{
    public const string DefaultGameRepoId = "release";

    /// <summary>Official entries (the user's builds first, upstream as fallback).</summary>
    public static IReadOnlyList<RepoEntry> Official { get; } =
    [
        new(
            "release",
            "Release (My Build)",
            "https://github.com/saintromuald/cosmonarchy-bw-release.git",
            RepoKind.Game,
            BuildDir: "Release",
            // The mirror's `main` is the September snapshot; every build since
            // (gptp sync, plugins, lobby templates) lives on `plus`.
            Branch: "plus"
        ),
        new(
            "prerelease-mine",
            "Prerelease (My Build)",
            "https://github.com/saintromuald/cosmonarchy-bw-prerelease.git",
            RepoKind.Game,
            BuildDir: "Prerelease",
            Branch: "plus"
        ),
        new(
            "prerelease",
            "Prerelease (Upstream)",
            "https://gitlab.com/the-no-frauds-club/cosmonarchy-bw-prerelease.git",
            RepoKind.Game,
            BuildDir: "Prerelease"
        ),
        new(
            "release-upstream",
            "Release (Upstream)",
            "https://gitlab.com/the-no-frauds-club/cosmonarchy-bw-release.git",
            RepoKind.Game,
            BuildDir: "Release"
        ),
    ];

    /// <summary>Community mirror entries, hidden unless requested.</summary>
    public static IReadOnlyList<RepoEntry> Unofficial { get; } =
    [
        new(
            "luci-release",
            "Release (Unofficial Mirror)",
            "https://gitea.aromavir.us/lucifirius/cosmonarchy-bw-release.git",
            RepoKind.Game,
            BuildDir: "Release",
            Unofficial: true
        ),
        new(
            "luci-prerelease",
            "Prerelease (Unofficial Mirror)",
            "https://gitea.aromavir.us/lucifirius/cosmonarchy-bw-prerelease.git",
            RepoKind.Game,
            BuildDir: "Prerelease",
            Unofficial: true
        ),
    ];

    /// <summary>Map data used by every game entry.</summary>
    public static RepoEntry Maps { get; } = new(
        "cmbw-root",
        "CMBW-Root Maps",
        "https://gitlab.com/the-no-frauds-club/cmbw-root.git",
        RepoKind.Maps
    );

    /// <summary>Plugin repositories: the Plus gameplay fork and the rollback
    /// netcode. Staged as zz-cosmo-plus.qdp / zz-cosmo-rollback.qdp.</summary>
    public static IReadOnlyList<RepoEntry> Plugins { get; } =
    [
        new(
            "plus",
            "Cosmonarchy Plus",
            "https://github.com/saintromuald/cosmonarchy-plus.git",
            RepoKind.Plugin,
            BuildDir: "zz-cosmo-plus.qdp"
        ),
        new(
            "rollback",
            "Cosmonarchy Rollback",
            "https://github.com/saintromuald/cosmonarchy-rollback.git",
            RepoKind.Plugin,
            BuildDir: "zz-cosmo-rollback.qdp"
        ),
    ];

    public static RepoEntry? Find(string id) =>
        Official.Concat(Unofficial).Append(Maps).Concat(Plugins)
               .FirstOrDefault(r => r.Id == id);
}
