using System.Diagnostics;
using System.IO.Compression;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>The install/update/play pipeline, scoped to one installed game
/// version: shared base (StarCraft -> remote patches -> CrownLink -> maps ->
/// Wine fix), then the version's own game checkout -> variant staging ->
/// launch. All versions under the same root share the base.</summary>
public sealed class InstallService
{
    private readonly AppSettings _settings;
    private readonly RepoEntry _repo;
    private readonly GameVersion _version;
    private readonly string _mapsUrl;
    private readonly PatchManifestService _patches = new();
    private readonly CrownLinkService _crownLink = new();

    public InstallService(AppSettings settings, RepoEntry repo, GameVersion version, string? mapsUrl = null)
    {
        _settings = settings;
        _repo = repo;
        _version = version;
        _mapsUrl = mapsUrl ?? RepoCatalog.Maps.Url;
    }

    public string InstallRoot => _settings.RootPath;
    public string StarcraftDir => Path.Combine(InstallRoot, "Starcraft");
    public string StarcraftExe => Path.Combine(StarcraftDir, "StarCraft.exe");
    public string RepoDir => Path.Combine(InstallRoot, _version.Dir);
    public string MapsDir
    {
        get
        {
            // Windows is case-insensitive; on Linux the historical installs
            // use Starcraft/Maps (capital M) while fresh installs may use
            // Starcraft/maps. Prefer whichever already exists.
            var lower = Path.Combine(StarcraftDir, "maps", "CMBW-Root");
            if (Directory.Exists(lower))
                return lower;
            return Path.Combine(StarcraftDir, "Maps", "CMBW-Root");
        }
    }

    /// <summary>The directory the game watches for replay files.</summary>
    public string ReplayGameDir
    {
        get
        {
            var lower = Path.Combine(StarcraftDir, "maps", "replays");
            if (Directory.Exists(lower))
                return lower;
            return Path.Combine(StarcraftDir, "Maps", "replays");
        }
    }
    public string CrownLinkSnp => Path.Combine(StarcraftDir, "CrownLink.snp");
    public string GameExe => File.Exists(Path.Combine(RepoDir, "Cosmonarchy.exe"))
        ? Path.Combine(RepoDir, "Cosmonarchy.exe")
        : Path.Combine(RepoDir, "Cosmonarchy BW.exe");
    public string StageDir => InstallPaths.StageDir(_settings);

    /// <summary>Install/update this version: ensure the shared base, then
    /// bring the version's own checkout to its tracked branch tip or its
    /// pinned commit.</summary>
    public async Task InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default,
        bool forceCheckout = false)
    {
        await EnsureBaseAsync(progress, ct);
        await EnsureVersionAsync(progress, ct, forceCheckout);
        HealTempMpq(progress);
    }

    public async Task PlayAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await InstallAsync(progress, ct);
        // The version's own family decides the staging: an Original install
        // never picks up the Plus plugin just because the Play page's filter
        // was left on Plus (and a Netcode install always arms rollback).
        var variant = _version.EffectiveVariant;
        var role = variant switch
        {
            Variant.Original => null,
            // Rollback only has host (slot 0) and guest (slot 1); Solo is a
            // Plus concept and must not arm slot 0 as a "solo" netcode game.
            Variant.Netcode => _settings.NetcodeRole == NetcodeRole.Guest
                ? NetcodeRole.Guest
                : NetcodeRole.Host,
            _ => _settings.NetcodeRole,
        };
        var stager = new PluginStager(RepoDir, StageDir,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        stager.Apply(variant, role);
        progress?.Report($"Launching {_version.Name} ({variant})");
        EnsureFullscreenDisplay(progress);
        await LaunchAsync(ct);
    }

    /// <summary>Copy a replay into the game's replay directory and launch
    /// into it. Replays are played on the vanilla build (the Plus plugin is
    /// unstaged and rollback disarmed), matching how they were recorded.</summary>
    public async Task PlayReplayAsync(string replayPath, IProgress<string>? progress = null,
        CancellationToken ct = default, bool forceCheckout = false, bool launch = true)
    {
        await InstallAsync(progress, ct, forceCheckout);
        var stager = new PluginStager(RepoDir, StageDir,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        stager.Apply(Variant.Original, null);

        var destination = Path.Combine(ReplayGameDir, Path.GetFileName(replayPath));
        if (!string.Equals(Path.GetFullPath(destination), Path.GetFullPath(replayPath), StringComparison.Ordinal))
        {
            Directory.CreateDirectory(ReplayGameDir);
            File.Copy(replayPath, destination, overwrite: true);
        }

        if (!launch)
        {
            progress?.Report($"Replay ready: {Path.GetFileName(destination)}");
            return;
        }

        progress?.Report($"Launching replay {Path.GetFileName(replayPath)}");
        EnsureFullscreenDisplay(progress);
        await LaunchAsync(ct, destination);
    }

    private async Task EnsureBaseAsync(IProgress<string>? progress, CancellationToken ct)
    {
        await EnsureStarcraftAsync(progress, ct);
        await ApplyPatchesAsync(progress, ct);
        await InstallCrownLinkAsync(progress, ct);
        await EnsureMapsAsync(progress, ct);
        FixWineRegistry(progress);
    }

    private async Task EnsureVersionAsync(IProgress<string>? progress, CancellationToken ct,
        bool forceCheckout = false)
    {
        using var repo = new RepoManager(RepoDir, _repo.Url);
        if (repo.IsInstalled())
        {
            var remoteName = repo.EnsureRemoteName();
            if (!forceCheckout && repo.HasLocalChanges())
            {
                // Never clobber local edits (hotloader work, tweaked
                // plugins): launch or play with the checkout as it is.
                progress?.Report("Local changes detected; using the checkout as-is");
                return;
            }

            repo.Fetch(progress);
            if (_version.TracksBranch)
            {
                var branch = string.IsNullOrWhiteSpace(_repo.Branch) ? "main" : _repo.Branch;
                repo.CheckoutBranch(branch, remoteName, progress);
            }
            else
            {
                repo.CheckoutCommit(_version.PinnedSha!, progress);
            }
        }
        else
        {
            if (Directory.Exists(RepoDir))
                Directory.Delete(RepoDir, recursive: true);
            repo.Clone(progress);
            if (_version.TracksBranch)
            {
                // A plain clone checks out the remote's default branch. The
                // catalog builds live on `plus` (Plus mirror), `master`
                // (archives) or `main`, so installing "Latest" must land on
                // the entry's branch immediately, not only after the next
                // update.
                var branch = string.IsNullOrWhiteSpace(_repo.Branch) ? "main" : _repo.Branch;
                var remoteName = repo.EnsureRemoteName();
                repo.CheckoutBranch(branch, remoteName, progress);
            }
            else
            {
                repo.CheckoutCommit(_version.PinnedSha!, progress);
            }
        }
        await Task.CompletedTask; // git operations are sync in LibGit2Sharp
    }

    private async Task EnsureStarcraftAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (File.Exists(StarcraftExe))
            return;

        var zip = Path.Combine(InstallRoot, "starcraft.zip");
        Directory.CreateDirectory(InstallRoot);
        using var http = new HttpClient();
        await HttpDownload.ToFileAsync(http, RemoteAssets.StarcraftZip, zip, progress, "Downloading StarCraft", ct);

        progress?.Report("Extracting StarCraft");
        ZipFile.ExtractToDirectory(zip, InstallRoot, overwriteFiles: true);
        File.Delete(zip);

        _settings.PatchesInstalled.Clear();
        _settings.Save();
    }

    private async Task ApplyPatchesAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var manifest = await _patches.LoadAsync(ct);
        var installed = _settings.PatchesInstalled;
        foreach (var patch in manifest)
        {
            if (installed.Contains(patch.Id))
                continue;
            using var http = new HttpClient();
            var target = Path.Combine(InstallRoot, string.IsNullOrEmpty(patch.Filename)
                ? Path.GetFileName(new Uri(patch.Url).AbsolutePath)
                : patch.Filename);
            var label = string.IsNullOrEmpty(patch.Filename)
                ? $"Downloading patch {patch.Id}"
                : $"Downloading {patch.Filename}";
            await HttpDownload.ToFileAsync(http, patch.Url, target, progress, label, ct);

            if (target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(target, InstallRoot, overwriteFiles: true);
                File.Delete(target);
            }

            installed.Add(patch.Id);
            _settings.Save();
        }
    }

    private async Task InstallCrownLinkAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var (url, size, id) = await _crownLink.GetLatestSnpAsync(ct);
        if (File.Exists(CrownLinkSnp) && new FileInfo(CrownLinkSnp).Length == size
            && _settings.CrownLinkAssetId == id)
            return;

        using var http = new HttpClient();
        await HttpDownload.ToFileAsync(http, url, CrownLinkSnp, progress, "Downloading CrownLink", ct);
        _settings.CrownLinkAssetId = id;
        _settings.Save();
    }

    private async Task EnsureMapsAsync(IProgress<string>? progress, CancellationToken ct)
    {
        using var maps = new RepoManager(MapsDir, _mapsUrl);
        if (maps.IsInstalled())
        {
            var remoteName = maps.EnsureRemoteName();
            maps.Fetch(progress);
            maps.ResetTo($"{remoteName}/main", progress);
        }
        else
        {
            if (Directory.Exists(MapsDir))
                Directory.Delete(MapsDir, recursive: true);
            maps.Clone(progress);
        }
        await Task.CompletedTask;
    }

    /// <summary>Under Wine, the game needs ddraw overridden to
    /// native,builtin. On real Windows this is a no-op.</summary>
    private void FixWineRegistry(IProgress<string>? progress)
    {
        if (OperatingSystem.IsWindows())
            return;

        var wineUserReg = Path.Combine(_settings.LinuxPrefixRoot, "prefix", "user.reg");
        if (!File.Exists(wineUserReg))
            return;

        var text = File.ReadAllText(wineUserReg);
        if (text.Contains(@"[Software\\Wine\\DllOverrides]", StringComparison.Ordinal))
            return; // key exists; assume ddraw override is present

        progress?.Report("Setting Wine ddraw override");
        File.AppendAllText(wineUserReg,
            "\n[Software\\\\Wine\\\\DllOverrides] 1759553452\n\"ddraw\"=\"native,builtin\"\n");
    }

    /// <summary>Heal a truncated temp.mpq left by an interrupted hotloader
    /// rebuild: it is removed so the game rebuilds it on this launch (black
    /// screen for about a minute while it packs).</summary>
    private void HealTempMpq(IProgress<string>? progress)
    {
        var mpq = Path.Combine(RepoDir, "temp.mpq");
        if (!File.Exists(mpq))
            return;
        var tree = Path.Combine(RepoDir, "mpq");
        if (TempMpqChecker.IsComplete(mpq, tree))
            return;

        File.Delete(mpq);
        progress?.Report(
            "Removed incomplete temp.mpq; the game rebuilds it now (black screen for about a minute — let it finish).");
    }

    /// <summary>Force fullscreen for Play launches. cnc-ddraw reads ddraw.ini
    /// from the game folder (next to the exe, or in the Starcraft dir), so the
    /// display keys are set in both places. See <see cref="DisplayIni"/>.</summary>
    private void EnsureFullscreenDisplay(IProgress<string>? progress)
    {
        progress?.Report("Setting fullscreen display");
        foreach (var dir in new[] { RepoDir, StarcraftDir })
        {
            var path = Path.Combine(dir, "ddraw.ini");
            if (File.Exists(path))
                File.WriteAllText(path, DisplayIni.Patch(File.ReadAllText(path)));
            else
                File.WriteAllText(path, DisplayIni.Fullscreen);
        }
    }

    private async Task LaunchAsync(CancellationToken ct, string? replayPath = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo(GameExe)
            {
                WorkingDirectory = RepoDir,
                UseShellExecute = true,
            };
            if (replayPath is not null)
                psi.ArgumentList.Add(replayPath);
            Process.Start(psi);
            await WaitForStarcraftWindowAsync(ct);
            return;
        }

        var exe = $@"C:\Cosmonarchy\{_version.Dir.Replace('/', '\\')}\Cosmonarchy BW.exe";
        var startInfo = new ProcessStartInfo("umu-run")
        {
            WorkingDirectory = RepoDir,
            UseShellExecute = false,
            ArgumentList = { exe },
        };
        if (replayPath is not null)
            startInfo.ArgumentList.Add(ToWindowsPath(replayPath));
        startInfo.Environment["WINEPREFIX"] = Path.Combine(_settings.LinuxPrefixRoot, "prefix");
        startInfo.Environment["PROTONPATH"] = "GE-Proton";
        // The channel name (Release/Prerelease/Original) mirrors cosmo-variant
        // and cosmo-watch. A GAMEID that does not match the channel's casing
        // makes umu/proton treat the game as a different title, which ended
        // the session cleanly after ~30 seconds.
        startInfo.Environment["GAMEID"] = $"umu-cosmonarchy-{ChannelName(_version.Dir)}";
        startInfo.Environment["PROTON_VERB"] = "waitforexitandrun";

        // Docked play: a scale-2 handheld panel + scale-1 external monitor mix
        // into one Xwayland layout that the game miscomputes (broken window
        // size, and the X pointer freezes when the real pointer crosses the
        // seam). With the panel disabled the game sees one clean screen, the
        // same workaround cosmo-variant applies for the script launchers.
        var panel = DisablePanelForDockedPlay();
        var game = Process.Start(startInfo);
        // Restore the panel when the game exits, without keeping the Play
        // flow busy: the launcher stays usable while the game runs.
        if (panel is not null)
            _ = RestorePanelWhenExitedAsync(game, panel.Value);

        // Return once the game process is up: the launcher stays usable to
        // switch versions while the game runs.
        await Task.CompletedTask;
    }

    /// <summary>If Hyprland is running with the handheld panel (eDP-1) and an
    /// external display (HDMI-A-1) both active, disable the panel for the
    /// duration of play and return true; otherwise return null.</summary>
    private bool? DisablePanelForDockedPlay()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        const string hyprctl = "/usr/bin/hyprctl";
        if (!File.Exists(hyprctl))
            return null;
        try
        {
            var info = new ProcessStartInfo(hyprctl)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "monitors", "-j" },
            };
            using var proc = Process.Start(info);
            if (proc is null)
                return null;
            var json = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            bool hdmi = false, panel = false;
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                if (!m.TryGetProperty("name", out var name))
                    continue;
                var disabled = m.TryGetProperty("disabled", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True;
                if (name.GetString() == "HDMI-A-1" && !disabled)
                    hdmi = true;
                if (name.GetString() == "eDP-1" && !disabled)
                    panel = true;
            }
            if (!hdmi || !panel)
                return null;
            return RunHyprctl("eval", "hl.monitor({output='eDP-1', disabled=true})");
        }
        catch
        {
            return null;
        }
    }

    private static async Task RestorePanelWhenExitedAsync(Process game, bool changed)
    {
        try
        {
            await game.WaitForExitAsync();
        }
        catch
        {
            // The game process handle may be gone; restore regardless.
        }
        if (changed)
            RunHyprctl("eval", "hl.monitor({output='eDP-1', disabled=false})");
    }

    private static bool RunHyprctl(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("/usr/bin/hyprctl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
                info.ArgumentList.Add(a);
            using var proc = Process.Start(info);
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Map a file inside the Wine prefix's drive_c to its Windows
    /// path, so the game can be handed a replay argument.</summary>
    private string ToWindowsPath(string path)
    {
        var driveC = Path.Combine(_settings.LinuxPrefixRoot, "prefix", "drive_c");
        var relative = Path.GetRelativePath(driveC, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            return path;
        return "C:\\" + relative.Replace('/', '\\');
    }

    /// <summary>The build channel a checkout belongs to: the first path
    /// segment of its directory ("Release", "Prerelease", "Original",
    /// "versions", …). Used for the umu GAMEID so every launch of the same
    /// channel shares one proton identity.</summary>
    internal static string ChannelName(string dir)
    {
        var trimmed = dir.Trim('/');
        var slash = trimmed.IndexOfAny(['/', '\\']);
        var first = slash < 0 ? trimmed : trimmed[..slash];
        return string.IsNullOrEmpty(first) ? "Release" : first;
    }

    private static async Task WaitForStarcraftWindowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var proc in Process.GetProcessesByName("Starcraft"))
            {
                try
                {
                    if (!string.IsNullOrEmpty(proc.MainWindowTitle))
                        return;
                }
                catch (InvalidOperationException)
                {
                }
            }
            await Task.Delay(500, ct);
        }
    }
}
