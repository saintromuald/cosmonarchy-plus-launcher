using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Services;

/// <summary>Process-wide singletons. Keeps the UI free of service wiring;
/// every view model reads the same settings instance and saves through it.</summary>
public static class AppRuntime
{
    public static AppSettings Settings { get; } = AppSettings.Load();

    public static VersionManager Versions { get; } = new(Settings);

    public static AccountService Account { get; } = new();

    static AppRuntime()
    {
        // Adopt any existing checkouts (legacy Release/Prerelease, leftover
        // versions/*) the first time the new version list is seen.
        Versions.AdoptExistingInstallations();
    }

    public static InstallService CreateInstaller(RepoEntry repo, GameVersion version) =>
        new(Settings, repo, version);
}
