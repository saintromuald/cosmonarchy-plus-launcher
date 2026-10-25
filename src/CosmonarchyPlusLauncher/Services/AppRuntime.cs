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

    /// <summary>Where statistics accounts live. Local today; ROADMAP.md
    /// specifies the server implementation that replaces this later.</summary>
    public static IAccountBackend AccountBackend { get; } = new LocalAccountBackend();

    /// <summary>The main window, set by MainWindow on construction. Used to
    /// minimize the launcher out of the way when a replay starts.</summary>
    public static Avalonia.Controls.Window? MainWindow { get; set; }

    /// <summary>Put the launcher out of the way so the fullscreen game is
    /// immediately visible (replay watching, play launches).</summary>
    public static void MinimizeMainWindow()
    {
        if (MainWindow is { } window)
        {
            try
            {
                window.WindowState = Avalonia.Controls.WindowState.Minimized;
            }
            catch
            {
                // Best effort; the fullscreen game covers the launcher anyway.
            }
        }
    }

    /// <summary>The statistics account signed in on this machine (email +
    /// password account that claims an in-game name and certifies matches).</summary>
    public static LocalAccount? CurrentLocalAccount { get; private set; }

    /// <summary>Raised when the local account logs in / out / edits its name;
    /// the Statistics tab listens to rebuild itself.</summary>
    public static event Action? LocalAccountChanged;

    public static void SetLocalAccount(LocalAccount? account)
    {
        CurrentLocalAccount = account;
        Settings.ActiveLocalAccountId = account?.Id;
        Settings.Save();
        LocalAccountChanged?.Invoke();
    }

    /// <summary>Raised whenever a match certification changes so the Replays
    /// tab can refresh its badges and the Statistics tab its numbers.</summary>
    public static event Action? CertificationsChanged;

    public static void NotifyCertificationsChanged() => CertificationsChanged?.Invoke();

    /// <summary>Set from MainWindow once the surface exists; used by
    /// "Share note"/"Share list" to copy exports.</summary>
    public static Avalonia.Input.Platform.IClipboard? Clipboard { get; set; }

    static AppRuntime()
    {
        // Adopt any existing checkouts (legacy Release/Prerelease, leftover
        // versions/*) the first time the new version list is seen.
        Versions.AdoptExistingInstallations();

        // Restore the statistics account that was signed in last time.
        if (AccountBackend is LocalAccountBackend localBackend)
        {
            var restored = !string.IsNullOrEmpty(Settings.ActiveLocalAccountId)
                ? localBackend.FindAccountById(Settings.ActiveLocalAccountId)
                : null;
            if (restored is null)
            {
                // Pre-persistence builds never saved the active account id.
                // When the machine holds exactly one account, that one is it.
                var accounts = localBackend.ListAccounts();
                if (accounts.Count == 1)
                    restored = accounts[0];
            }
            if (restored is not null)
            {
                CurrentLocalAccount = restored;
                if (!string.Equals(Settings.ActiveLocalAccountId, restored.Id, StringComparison.Ordinal))
                {
                    Settings.ActiveLocalAccountId = restored.Id;
                    Settings.Save();
                }
            }
        }
    }

    public static InstallService CreateInstaller(RepoEntry repo, GameVersion version) =>
        new(Settings, repo, version);
}
