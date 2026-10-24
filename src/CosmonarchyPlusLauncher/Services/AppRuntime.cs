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

    /// <summary>The replay library shared by the Replays and Favourites tabs.
    /// Save through <see cref="SaveLibrary"/> so every tab refreshes.</summary>
    public static ReplayLibraryStore LibraryStore { get; } = new();

    public static ReplayLibrary Library { get; } = LibraryStore.Load();

    /// <summary>Raised after <see cref="SaveLibrary"/>; both tabs listen and
    /// rebuild their views so favourites, notes and lists stay in sync.</summary>
    public static event Action? LibraryChanged;

    public static void SaveLibrary()
    {
        LibraryStore.Save(Library);
        LibraryChanged?.Invoke();
    }

    /// <summary>The logged-in cosmonarchy.com profile, used to stamp the
    /// sharer's Discord name on published shares. Set by the Account page.</summary>
    public static User? CurrentUser { get; set; }

    public static event Action? CurrentUserChanged;

    public static void SetCurrentUser(User? user)
    {
        CurrentUser = user;
        CurrentUserChanged?.Invoke();
    }

    /// <summary>Set from MainWindow once the surface exists; used by
    /// "Share note"/"Share list" to copy exports.</summary>
    public static Avalonia.Input.Platform.IClipboard? Clipboard { get; set; }

    static AppRuntime()
    {
        // Adopt any existing checkouts (legacy Release/Prerelease, leftover
        // versions/*) the first time the new version list is seen.
        Versions.AdoptExistingInstallations();
    }

    public static InstallService CreateInstaller(RepoEntry repo, GameVersion version) =>
        new(Settings, repo, version);
}
