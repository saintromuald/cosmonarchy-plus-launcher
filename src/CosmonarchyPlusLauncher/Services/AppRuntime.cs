using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Services;

/// <summary>Process-wide singletons. Keeps the UI free of service wiring;
/// every view model reads the same settings instance and saves through it.</summary>
public static class AppRuntime
{
    public static AppSettings Settings { get; } = AppSettings.Load();

    public static InstallService CreateInstaller(RepoEntry repo) => new(Settings, repo);

    public static AccountService Account { get; } = new();
}
