using System.Text.Json;
using System.Text.Json.Serialization;
using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Settings;

/// <summary>Persisted launcher settings. Stored as JSON in the platform
/// config directory. The original launcher keeps equivalents in the Windows
/// registry; a portable JSON file works for both Linux and Windows.</summary>
public sealed class AppSettings
{
    public const string CurrentVersion = "1";

    public string SettingsVersion { get; set; } = CurrentVersion;

    /// <summary>Install root. Windows: C:\Cosmonarchy\.
    /// Linux: the drive_c/Cosmonarchy path inside the Wine prefix.</summary>
    public string RootPath { get; set; } = PlatformDefaults.InstallRoot();

    public string SelectedRepoId { get; set; } = DefaultRepoId;

    /// <summary>The installed game versions. Each has its own checkout
    /// directory under the install root; they share the StarCraft base and
    /// the maps. Populated by adopting existing checkouts on first launch
    /// and by the install form in the UI.</summary>
    public List<GameVersion> Versions { get; set; } = [];

    /// <summary>Installed plugin builds (the Plus and Netcode families).
    /// Each record owns the .qdp files it put in the variants directory.</summary>
    public List<PluginVersion> Plugins { get; set; } = [];

    /// <summary>Which installed version the Play/Update/Delete buttons and
    /// the Replays page act on.</summary>
    public string? SelectedVersionId { get; set; }

    public bool ShowUnofficialRepos { get; set; }

    public Variant Variant { get; set; } = Variant.Original;

    public NetcodeRole? NetcodeRole { get; set; }

    public ActionOnPlay ActionOnPlay { get; set; } = ActionOnPlay.StayOpen;

    public bool FullVersionLog { get; set; }

    /// <summary>cosmonarchy.com bearer token for the account page.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountToken { get; set; }

    /// <summary>Id of the statistics account signed in on this machine.
    /// Restored on startup so sign-in persists; null = nobody signed in.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveLocalAccountId { get; set; }

    /// <summary>Patch ids already installed, so they are not re-applied.</summary>
    public List<string> PatchesInstalled { get; set; } = [];

    /// <summary>Players whose replays the Replays page should surface.</summary>
    public List<string> FollowedPlayers { get; set; } = [];

    /// <summary>Asset id of the last-installed CrownLink release, for caching.</summary>
    public int CrownLinkAssetId { get; set; }

    /// <summary>Whether the hidden Remastered launch was unlocked on this
    /// machine (see RemasteredAccess). The unlock password is never stored;
    /// only this flag is persisted.</summary>
    public bool RemasteredUnlocked { get; set; }

    /// <summary>Override URL (or local path) of the encrypted Remastered
    /// bundle. Null/empty = resolve the latest GitHub release asset.</summary>
    public string? RemasteredBundleUrl { get; set; }

    /// <summary>Bundle channel: "release" (stable) or "prerelease".
    /// The launcher always fetches the newest asset of the chosen channel.</summary>
    public string RemasteredChannel { get; set; } = "release";

    /// <summary>Optional Discord webhook URL (channel → Integrations →
    /// Webhooks) for posting shared notes/lists straight to a channel.
    /// Sharing also works without one, via the clipboard.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiscordWebhookUrl { get; set; }

    /// <summary>The public GitHub repo that hosts the community share board
    /// ("owner/name"). The Community tab reads it without authentication.</summary>
    public string CommunityRepo { get; set; } = "saintromuald/cosmonarchy-shares";

    /// <summary>Optional GitHub token for publishing to the community repo:
    /// a fine-grained token with "Contents: read and write" on that one repo,
    /// or a classic token with repo scope. Never needed for reading.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GitHubToken { get; set; }

    public string LinuxPrefixRoot { get; set; } = PlatformDefaults.LinuxPrefixRoot();

    // The Core project cannot reference the UI assembly; the default repo id
    // lives here to keep the dependency direction clean.
    public const string DefaultRepoId = "release";

    /// <summary>The file this instance was loaded from (or last saved to).
    /// Save() writes back here unless a path is passed explicitly, so callers
    /// that build settings in memory (tests, smoke runs) never overwrite the
    /// user's real settings file.</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    public static AppSettings Load(string? path = null)
    {
        path ??= PlatformDefaults.SettingsFile();
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
                loaded.FilePath = path;
                return loaded;
            }
            catch (JsonException)
            {
                // Corrupt settings: keep the file, start fresh in memory.
            }
        }
        return new AppSettings { FilePath = path };
    }

    public void Save(string? path = null)
    {
        path ??= FilePath ?? PlatformDefaults.SettingsFile();
        FilePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }
}

public enum ActionOnPlay
{
    StayOpen,
    Minimize,
    Close,
}

public static class PlatformDefaults
{
    public static string LinuxPrefixRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "cosmonarchy");

    public static string InstallRoot() =>
        OperatingSystem.IsWindows()
            ? @"C:\Cosmonarchy\"
            : Path.Combine(
                LinuxPrefixRoot(), "prefix", "drive_c", "Cosmonarchy");

    public static string SettingsFile() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CosmonarchyPlusLauncher", "settings.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "cosmonarchy-plus-launcher", "settings.json");

    /// <summary>Per-user data directory (caches, library, logs).</summary>
    public static string DataDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cosmonarchy-plus-launcher");
}
