using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Paths shared by the game install and the plugin installs.
/// Plugins stage outside the game tree (like <c>cosmo-variant</c>'s
/// <c>~/Games/cosmonarchy/variants</c>) so one copy serves every version.</summary>
public static class InstallPaths
{
    /// <summary>Where .qdp plugin builds live: under the Linux prefix root
    /// on Linux, or next to the game on Windows.</summary>
    public static string StageDir(AppSettings settings) => OperatingSystem.IsWindows()
        ? Path.Combine(settings.RootPath, "variants")
        : Path.Combine(settings.LinuxPrefixRoot, "variants");

    /// <summary>Checkouts of plugin repositories (only used by plugins that
    /// install from a git source instead of a release asset).</summary>
    public static string PluginCheckoutRoot(AppSettings settings) =>
        Path.Combine(settings.RootPath, "plugins");

    public static string PluginCheckoutDir(AppSettings settings, string id) =>
        Path.Combine(PluginCheckoutRoot(settings), id);
}
