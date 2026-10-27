using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>How this copy of the launcher was installed, which decides how
/// (or whether) it can replace itself.</summary>
public enum InstallKind
{
    /// <summary>A self-contained publish directory with the apphost and the
    /// deps file (the ~/.local/opt layout). Can swap itself in place.</summary>
    LinuxDirectory,

    /// <summary>A single-file .exe publish. Can replace itself after exit.</summary>
    WindowsSingleFile,

    /// <summary>Running from a mounted AppImage: updates come from a new
    /// AppImage, so the release page is offered instead.</summary>
    AppImage,

    /// <summary>Package-managed or unknown layout: updating is the package
    /// manager's job.</summary>
    Other,
}

/// <summary>An update that was downloaded, verified and staged. Hand to
/// <see cref="SelfUpdater.ApplyAndExit"/>: it launches the detached apply
/// script (which waits for this process to exit, swaps the files and
/// relaunches) and then terminates the app.</summary>
public sealed record PreparedUpdate(string Command, IReadOnlyList<string> Arguments);

/// <summary>In-place self-update from GitHub Releases: download the
/// platform asset, stage it next to the install, then swap and relaunch via
/// a detached script. The previous version is kept as a backup
/// (<c>.cosmonarchy-plus-launcher-backup</c>) until the next update.</summary>
public sealed class SelfUpdater
{
    public const string BinaryName = "CosmonarchyPlusLauncher";
    public const string UpdateDirName = ".cosmonarchy-plus-launcher-update";
    public const string BackupDirName = ".cosmonarchy-plus-launcher-backup";

    private readonly HttpClient _http;

    public SelfUpdater(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

    public static string AppDirectory() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static InstallKind GetInstallKind(string? installDir = null)
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 })
            return InstallKind.AppImage;
        installDir ??= AppDirectory();
        if (OperatingSystem.IsWindows())
        {
            return File.Exists(Path.Combine(installDir, BinaryName + ".exe"))
                ? InstallKind.WindowsSingleFile
                : InstallKind.Other;
        }
        return File.Exists(Path.Combine(installDir, BinaryName)) &&
               File.Exists(Path.Combine(installDir, BinaryName + ".deps.json"))
            ? InstallKind.LinuxDirectory
            : InstallKind.Other;
    }

    /// <summary>Downloads and stages the update for the current install kind.
    /// Throws with a user-readable message when this install cannot update
    /// itself.</summary>
    public async Task<PreparedUpdate> PrepareAsync(
        string installDir, UpdateInfo info, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (info.AssetUrl is null)
            throw new InvalidOperationException(
                "The release has no file for this platform — open the release page and install it manually.");
        return GetInstallKind(installDir) switch
        {
            InstallKind.LinuxDirectory => await PrepareLinuxDirectoryAsync(installDir, info, progress, ct),
            InstallKind.WindowsSingleFile => await PrepareWindowsSingleFileAsync(installDir, info, progress, ct),
            InstallKind.AppImage => throw new InvalidOperationException(
                "This install is an AppImage — download the new AppImage from the release page instead."),
            _ => throw new InvalidOperationException(
                "This install is managed elsewhere (package manager) — update it there, or open the release page."),
        };
    }

    private async Task<PreparedUpdate> PrepareLinuxDirectoryAsync(
        string installDir, UpdateInfo info, IProgress<string>? progress, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(installDir)
                     ?? throw new InvalidOperationException("Could not find the install directory's parent.");
        var work = Path.Combine(parent, UpdateDirName);
        var staging = Path.Combine(work, "staging");
        var download = Path.Combine(work, "update.tar.gz");
        Directory.CreateDirectory(work);

        await DownloadAsync(info, download, progress, ct);

        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        progress?.Report($"Extracting {info.AssetName}…");
        TarFile.ExtractToDirectory(download, staging, overwriteFiles: true);
        File.Delete(download);

        // The CI tarball may wrap the build in a subdirectory (e.g. linux/),
        // so locate the build directory instead of assuming a flat archive.
        var stagedBinary = Directory.EnumerateFiles(staging, BinaryName, SearchOption.AllDirectories)
            .FirstOrDefault();
        var stagedDeps = Directory.EnumerateFiles(staging, BinaryName + ".deps.json", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (stagedBinary is null || stagedDeps is null)
            throw new InvalidOperationException(
                "The update archive does not contain a launcher build — the release may be broken. Nothing was changed.");
        var buildDir = Path.GetDirectoryName(stagedBinary)!;
        // The CI tarball does not preserve the executable bit on the apphost
        // (the manual update hit "Permission denied" on first launch), so set
        // it before the swap: the apply script moves the whole build dir and
        // the relaunch must be able to exec the binary.
        try
        {
            File.SetUnixFileMode(stagedBinary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (IOException)
        {
            // Non-Unix or a filesystem without mode support: nothing to do.
        }

        var script = Path.Combine(work, "apply.sh");
        File.WriteAllText(script, LinuxApplyScript, new UTF8Encoding(false));
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var backup = Path.Combine(parent, BackupDirName);
        var binary = Path.Combine(installDir, BinaryName);
        return new PreparedUpdate("/bin/sh", new[]
        {
            script,
            Environment.ProcessId.ToString(),
            buildDir,
            installDir,
            backup,
            binary,
            work,
        });
    }

    private async Task<PreparedUpdate> PrepareWindowsSingleFileAsync(
        string installDir, UpdateInfo info, IProgress<string>? progress, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(installDir)
                     ?? throw new InvalidOperationException("Could not find the install directory's parent.");
        var work = Path.Combine(parent, UpdateDirName);
        var extract = Path.Combine(work, "extract");
        var download = Path.Combine(work, "update.zip");
        Directory.CreateDirectory(work);

        await DownloadAsync(info, download, progress, ct);

        if (Directory.Exists(extract))
            Directory.Delete(extract, recursive: true);
        Directory.CreateDirectory(extract);
        progress?.Report($"Extracting {info.AssetName}…");
        ZipFile.ExtractToDirectory(download, extract);
        File.Delete(download);

        var stagedBinary = Directory.EnumerateFiles(extract, BinaryName + ".exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (stagedBinary is null)
            throw new InvalidOperationException(
                "The update archive does not contain the launcher — the release may be broken. Nothing was changed.");

        var script = Path.Combine(work, "apply.cmd");
        File.WriteAllText(script, WindowsApplyScript, new UTF8Encoding(false));

        var target = Path.Combine(installDir, BinaryName + ".exe");
        return new PreparedUpdate("cmd.exe", new[] { "/c", script, Environment.ProcessId.ToString(), target, stagedBinary });
    }

    private async Task DownloadAsync(
        UpdateInfo info, string destination, IProgress<string>? progress, CancellationToken ct)
    {
        await HttpDownload.ToFileAsync(
            _http, info.AssetUrl, destination, progress, $"Downloading {info.AssetName}", ct);
    }

    /// <summary>Launches the detached apply script and terminates this
    /// process; the script waits for the exit, swaps the install and starts
    /// the new build.</summary>
    public static void ApplyAndExit(PreparedUpdate update)
    {
        var start = new ProcessStartInfo(update.Command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var argument in update.Arguments)
            start.ArgumentList.Add(argument);
        Process.Start(start);
        Environment.Exit(0);
    }

    /// <summary>Runs after this process exits: waits for the pid, swaps the
    /// staged directory over the install (keeping the old one as backup) and
    /// relaunches. Written with plain sh so it works on any distro.</summary>
    private const string LinuxApplyScript = """
        #!/bin/sh
        # Written by the Cosmonarchy Plus Launcher self-updater.
        PID="$1"; STAGING="$2"; INSTALL="$3"; BACKUP="$4"; BIN="$5"; WORK="$6"
        i=0
        while kill -0 "$PID" 2>/dev/null && [ "$i" -lt 600 ]; do sleep 0.1; i=$((i+1)); done
        rm -rf "$BACKUP"
        if mv "$INSTALL" "$BACKUP"; then
            if ! mv "$STAGING" "$INSTALL"; then
                mv "$BACKUP" "$INSTALL"
            fi
        fi
        rm -rf "$WORK"
        nohup "$BIN" >/dev/null 2>&1 &
        """;

    /// <summary>Best-effort Windows path: waits for the pid to vanish, then
    /// moves the staged exe over the running one and starts it. The single-file
    /// exe cannot be replaced while running, hence the wait loop.</summary>
    private const string WindowsApplyScript = """
        @echo off
        rem Written by the Cosmonarchy Plus Launcher self-updater.
        :wait
        timeout /t 1 /nobreak >nul
        tasklist /FI "PID eq %1" 2>nul | find "%1" >nul
        if not errorlevel 1 goto wait
        move /Y "%~3" "%~2" >nul
        start "" "%~2"
        exit /b
        """;
}
