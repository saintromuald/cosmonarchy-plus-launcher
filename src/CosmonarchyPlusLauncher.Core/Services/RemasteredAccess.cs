using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>
/// Access gate for the private StarCraft: Remastered (Samase) launch.
///
/// The launcher itself is open source: this file intentionally contains no
/// secrets, only a SHA-256 hash of the unlock password and the standard path
/// of the private bundle script. Unlocking requires the password (which is
/// never stored or transmitted); launching additionally requires the private
/// bundle to be installed locally — it is delivered out-of-band, never
/// through the launcher or the AUR package.
/// </summary>
public static class RemasteredAccess
{
    /// <summary>SHA-256 (lowercase hex) of the unlock password.</summary>
    private const string PasswordHash =
        "7b2371fe306572ba59ed46298be879a2a0251cdf9248c868c2028143815e52cc";

    /// <summary>Standard location of the private launch bundle. The script
    /// launches StarCraft: Remastered through the local Battle.net prefix
    /// with the mod loaded; its contents are never part of this repo.</summary>
    public static string BundleScriptPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".local", "share", "cosmonarchy-remastered", "run-samase.sh");

    public static bool IsBundleInstalled =>
        OperatingSystem.IsLinux() && File.Exists(BundleScriptPath);

    public static bool IsUnlocked(AppSettings settings) =>
        settings.RemasteredUnlocked && IsBundleInstalled;

    /// <summary>Verify the unlock password. On success the unlock flag is
    /// persisted so the hidden Play button stays visible across launches.</summary>
    public static bool TryUnlock(AppSettings settings, string password)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)))
            .ToLowerInvariant();
        if (!string.Equals(hash, PasswordHash, StringComparison.Ordinal))
            return false;
        settings.RemasteredUnlocked = true;
        settings.Save();
        return true;
    }

    /// <summary>Run the private bundle. Only callable when unlocked and the
    /// bundle exists; the actual SC:R launch then goes through the player's
    /// own Battle.net login, as normal for that client.</summary>
    public static bool Launch(AppSettings settings, out string status)
    {
        if (!OperatingSystem.IsLinux())
        {
            status = "The Remastered launch is Linux-only for now.";
            return false;
        }
        if (!settings.RemasteredUnlocked)
        {
            status = "Remastered access is locked. Unlock it in Settings first.";
            return false;
        }
        if (!IsBundleInstalled)
        {
            status = "The Remastered bundle is not installed on this machine.";
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo("/bin/sh", Quote(BundleScriptPath))
            {
                UseShellExecute = false,
            };
            Process.Start(psi);
            status = "Launching StarCraft: Remastered…";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Could not start the Remastered launch: {ex.Message}";
            return false;
        }
    }

    private static string Quote(string arg) => $"\"{arg.Replace("\"", "\\\"")}\"";
}
