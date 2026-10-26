using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>One-time rename of stored display names written before the
/// Original CM / Plus / Original CM Netcode naming scheme. Display names are
/// derived from repository names, so records created under the old catalog
/// names are rewritten to the new ones once; ids, folders, and pins are
/// untouched.</summary>
public static class LegacyNames
{
    /// <summary>Old catalog name prefixes, by repository id.</summary>
    private static readonly IReadOnlyDictionary<string, string> OldRepoPrefixes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["release-upstream"] = "Release — Stable (Upstream)",
            ["prerelease"] = "Prerelease (Upstream)",
            ["original"] = "Cosmonarchy Original (pre-split archive)",
            ["old-release"] = "Old Release Archive (2023)",
            ["nightly"] = "2023 Nightly Archive",
            ["release"] = "Release — Plus (My Build)",
            ["prerelease-mine"] = "Prerelease — Plus (My Build)",
            ["luci-release"] = "Release — Stable (Luci mirror)",
            ["luci-prerelease"] = "Prerelease (Luci mirror)",
        };

    private static readonly IReadOnlyDictionary<string, string> OldPluginPrefixes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["plus"] = "Cosmonarchy Plus plugin",
            ["rollback"] = "Cosmonarchy Rollback plugin",
        };

    /// <summary>Rewrite legacy stored names. Returns true when the settings
    /// were changed (caller saves).</summary>
    public static bool Apply(AppSettings settings)
    {
        var changed = false;

        foreach (var version in settings.Versions)
        {
            var name = Rename(version.Name, version.RepoId, OldRepoPrefixes);
            if (name is not null && name != version.Name)
            {
                version.Name = name;
                changed = true;
            }
        }

        foreach (var plugin in settings.Plugins)
        {
            var name = Rename(plugin.Name, plugin.RepoId, OldPluginPrefixes);
            if (name is not null && name != plugin.Name)
            {
                plugin.Name = name;
                changed = true;
            }
        }

        return changed;
    }

    private static string? Rename(
        string name, string repoId, IReadOnlyDictionary<string, string> prefixes)
    {
        if (string.IsNullOrEmpty(repoId)
            || !prefixes.TryGetValue(repoId, out var oldPrefix))
            return null;

        var repo = RepoCatalog.Find(repoId);
        if (repo is null)
            return null;

        var renamed = name.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)
            ? repo.Name + name[oldPrefix.Length..]
            : name;

        // A Netcode-family install of another family's build carried a
        // " — Netcode" suffix (written by the old DefaultName).
        if (renamed.EndsWith(" — Netcode", StringComparison.Ordinal))
            renamed = renamed[..^" — Netcode".Length] + " — Original CM Netcode";
        else
            renamed = renamed.Replace(" — Netcode — ", " — Original CM Netcode — ",
                StringComparison.Ordinal);

        return renamed;
    }
}
