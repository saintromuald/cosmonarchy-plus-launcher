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

    public bool ShowUnofficialRepos { get; set; }

    public Variant Variant { get; set; } = Variant.Original;

    public NetcodeRole? NetcodeRole { get; set; }

    public ActionOnPlay ActionOnPlay { get; set; } = ActionOnPlay.StayOpen;

    public bool FullVersionLog { get; set; }

    /// <summary>cosmonarchy.com bearer token for the account page.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountToken { get; set; }

    /// <summary>Patch ids already installed, so they are not re-applied.</summary>
    public List<string> PatchesInstalled { get; set; } = [];

    /// <summary>Asset id of the last-installed CrownLink release, for caching.</summary>
    public int CrownLinkAssetId { get; set; }

    public string LinuxPrefixRoot { get; set; } = PlatformDefaults.LinuxPrefixRoot();

    // The Core project cannot reference the UI assembly; the default repo id
    // lives here to keep the dependency direction clean.
    public const string DefaultRepoId = "release";

    public static AppSettings Load(string? path = null)
    {
        path ??= PlatformDefaults.SettingsFile();
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
            }
            catch (JsonException)
            {
                // Corrupt settings: keep the file, start fresh in memory.
            }
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= PlatformDefaults.SettingsFile();
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
}
