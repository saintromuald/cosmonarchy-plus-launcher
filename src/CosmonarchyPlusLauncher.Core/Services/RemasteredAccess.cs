using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>
/// Access gate for the private StarCraft: Remastered (Samase) launch.
///
/// The launcher itself is open source: this file intentionally contains no
/// secrets. Two different password derivations are used, so publishing the
/// UI unlock hash does not leak the bundle encryption key:
///   - unlock check:   SHA-256(password + ":unlock")   (constant below)
///   - bundle decrypt: PBKDF2-HMAC-SHA256(password + ":bundle", salt)
///
/// The bundle itself is a small encrypted tarball (samase64.exe +
/// run-samase.sh — no Blizzard content) hosted at a URL; it is useless
/// without the password. The mod data it needs ships with the normal
/// launcher install, and the game client comes from the player's own
/// Battle.net install.
/// </summary>
public static class RemasteredAccess
{
    /// <summary>SHA-256 (lowercase hex) of password + ":unlock".</summary>
    private const string UnlockHash =
        "c3afbb845f5a9949d4053fd632fdf563a325d973afcaac5d5135e517bbf7aa1c";

    /// <summary>Bundle host repo: the encrypted payload lives as a GitHub
    /// release asset; the launcher resolves the newest asset of the selected
    /// channel ("release" / "prerelease") at unlock time.</summary>
    public const string BundleOwner = "saintromuald";
    public const string BundleRepo = "cosmonarchy-remastered-bundle";
    public const string BundleAssetName = "cosmonarchy-remastered.enc";

    /// <summary>Unlock channels.</summary>
    public const string ChannelRelease = "release";
    public const string ChannelPrerelease = "prerelease";

    private const int KdfIterations = 100_000;

    /// <summary>Standard location of the private launch bundle.</summary>
    public static string BundleScriptPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".local", "share", "cosmonarchy-remastered", "run-samase.sh");

    /// <summary>Installed bundle version marker (the GitHub release tag).</summary>
    public static string BundleVersionPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".local", "share", "cosmonarchy-remastered", "version.txt");

    /// <summary>A resolved channel release: download URL + identifying tag.</summary>
    public sealed record BundleRelease(string Url, string Tag);

    public static bool IsBundleInstalled =>
        OperatingSystem.IsLinux() && File.Exists(BundleScriptPath);

    public static bool IsUnlocked(AppSettings settings) =>
        settings.RemasteredUnlocked && IsBundleInstalled;

    /// <summary>Verify the unlock password; on success persist the flag and
    /// fetch/decrypt the bundle if it is not already installed.</summary>
    public static string TryUnlock(AppSettings settings, string password)
    {
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(password + ":unlock")))
            .ToLowerInvariant();
        if (!string.Equals(hash, UnlockHash, StringComparison.Ordinal))
            return "Wrong password.";

        settings.RemasteredUnlocked = true;
        settings.Save();

        if (IsBundleInstalled)
        {
            var localTag = File.Exists(BundleVersionPath)
                ? File.ReadAllText(BundleVersionPath).Trim()
                : "";
            var latest = ResolveLatest(settings.RemasteredChannel);
            if (latest.Tag == localTag)
                return "Unlocked on this machine (latest bundle).";
            var updated = EnsureBundle(settings, password, latest);
            return updated ? $"Unlocked; bundle updated to {latest.Tag}."
                           : "Unlocked, but the bundle update could not be fetched.";
        }

        var first = EnsureBundle(settings, password);
        return first ? "Unlocked; bundle installed."
                     : "Unlocked, but the bundle could not be fetched.";
    }

    /// <summary>Download, decrypt and extract the private bundle.</summary>
    public static bool EnsureBundle(AppSettings settings, string password,
        BundleRelease? resolved = null)
    {
        if (!OperatingSystem.IsLinux())
            return false;
        try
        {
            string url;
            string tag;
            if (!string.IsNullOrWhiteSpace(settings.RemasteredBundleUrl))
            {
                url = settings.RemasteredBundleUrl;
                tag = "manual";
            }
            else
            {
                var r = resolved ?? ResolveLatest(settings.RemasteredChannel);
                url = r.Url;
                tag = r.Tag;
            }
            var blob = Fetch(url);
            var plain = Decrypt(blob, password);
            var dest = Path.GetDirectoryName(BundleScriptPath)!;
            Directory.CreateDirectory(dest);
            using var ms = new MemoryStream(plain);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, dest, overwriteFiles: true);
            File.WriteAllText(BundleVersionPath, tag);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Resolve the newest bundle asset of the given channel from
    /// GitHub releases. Public repo, no auth needed; the payload is
    /// ciphertext and useless without the unlock password.</summary>
    public static BundleRelease ResolveLatest(string channel)
    {
        var client = new Octokit.GitHubClient(
            new Octokit.ProductHeaderValue("CosmonarchyPlusLauncher"));
        Octokit.Release release;
        if (channel == ChannelPrerelease)
        {
            var releases = client.Repository.Release
                .GetAll(BundleOwner, BundleRepo)
                .GetAwaiter().GetResult();
            release = releases.FirstOrDefault(r => r.Prerelease)
                ?? throw new InvalidOperationException("no prerelease bundle");
        }
        else
        {
            release = client.Repository.Release
                .GetLatest(BundleOwner, BundleRepo)
                .GetAwaiter().GetResult();
        }
        var asset = release.Assets.FirstOrDefault(a => a.Name == BundleAssetName)
            ?? throw new InvalidOperationException($"no {BundleAssetName} asset");
        return new BundleRelease(asset.BrowserDownloadUrl, release.TagName);
    }

    private static byte[] Fetch(string url)
    {
        if (url.StartsWith("http://") || url.StartsWith("https://"))
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5),
            };
            return http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        }
        return File.ReadAllBytes(url);
    }

    /// <summary>Container: "CRMB" | u8 version | u16 iters/1000 | salt(16)
    /// | nonce(12) | ciphertext | tag(16) (AES-256-GCM, tag appended).</summary>
    private static byte[] Decrypt(byte[] blob, string password)
    {
        if (blob.Length < 4 + 1 + 2 + 16 + 12 + 16 + 16)
            throw new InvalidDataException("bundle too small");
        if (blob[0] != (byte)'C' || blob[1] != (byte)'R' ||
            blob[2] != (byte)'M' || blob[3] != (byte)'B')
            throw new InvalidDataException("not a remastered bundle");
        int o = 4;
        byte version = blob[o++];
        ushort itersK = (ushort)(blob[o] | (blob[o + 1] << 8));
        o += 2;
        int iters = itersK * 1000;
        var salt = blob[o..(o + 16)]; o += 16;
        var nonce = blob[o..(o + 12)]; o += 12;
        var tag = blob[^16..];
        var ct = blob[o..^16];

        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password + ":bundle"), salt, iters,
            HashAlgorithmName.SHA256, 32);
        var plain = new byte[ct.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ct, tag, plain);
        return plain;
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
