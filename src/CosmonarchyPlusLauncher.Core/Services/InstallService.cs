using System.Diagnostics;
using System.IO.Compression;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>The install/update/play pipeline:
/// StarCraft base -> remote patches -> CrownLink -> game repo -> maps ->
/// variant staging -> launch.</summary>
public sealed class InstallService
{
    private readonly AppSettings _settings;
    private readonly RepoEntry _repo;
    private readonly string _mapsUrl;
    private readonly PatchManifestService _patches = new();
    private readonly CrownLinkService _crownLink = new();

    public InstallService(AppSettings settings, RepoEntry repo, string? mapsUrl = null)
    {
        _settings = settings;
        _repo = repo;
        _mapsUrl = mapsUrl ?? RepoCatalog.Maps.Url;
    }

    public string InstallRoot => _settings.RootPath;
    public string StarcraftDir => Path.Combine(InstallRoot, "Starcraft");
    public string StarcraftExe => Path.Combine(StarcraftDir, "StarCraft.exe");
    public string RepoDir => Path.Combine(InstallRoot, _repo.BuildDir ?? "Release");
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
    public string StageDir => OperatingSystem.IsWindows()
        ? Path.Combine(InstallRoot, "variants")
        : Path.Combine(_settings.LinuxPrefixRoot, "variants");

    public async Task InstallAsync(string? commitSha, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureStarcraftAsync(progress, ct);
        await ApplyPatchesAsync(progress, ct);
        await InstallCrownLinkAsync(progress, ct);
        await EnsureGameRepoAsync(commitSha, progress, ct);
        await EnsureMapsAsync(progress, ct);
        FixWineRegistry(progress);
        HealTempMpq(progress);
    }

    public async Task PlayAsync(string? commitSha, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await InstallAsync(commitSha, progress, ct);
        var stager = new PluginStager(RepoDir, StageDir,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        stager.Apply(_settings.Variant, _settings.NetcodeRole);
        progress?.Report($"Launching {_repo.Name}");
        await LaunchAsync(ct);
    }

    /// <summary>Copy a replay into the game's replay directory and launch
    /// into it. Replays are played on the vanilla build (the Plus plugin is
    /// unstaged and rollback disarmed), matching how they were recorded.</summary>
    public async Task PlayReplayAsync(string replayPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await InstallAsync(null, progress, ct);
        var stager = new PluginStager(RepoDir, StageDir,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        stager.Apply(Variant.Original, null);

        var destination = Path.Combine(ReplayGameDir, Path.GetFileName(replayPath));
        if (!string.Equals(Path.GetFullPath(destination), Path.GetFullPath(replayPath), StringComparison.Ordinal))
        {
            Directory.CreateDirectory(ReplayGameDir);
            File.Copy(replayPath, destination, overwrite: true);
        }

        progress?.Report($"Launching replay {Path.GetFileName(replayPath)}");
        await LaunchAsync(ct);
    }

    private async Task EnsureStarcraftAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (File.Exists(StarcraftExe))
            return;

        progress?.Report("Downloading StarCraft");
        var zip = Path.Combine(InstallRoot, "starcraft.zip");
        Directory.CreateDirectory(InstallRoot);
        using (var http = new HttpClient())
        using (var res = await http.GetAsync(RemoteAssets.StarcraftZip, ct))
        using (var fs = File.Create(zip))
        {
            res.EnsureSuccessStatusCode();
            await res.Content.CopyToAsync(fs, ct);
        }

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
            progress?.Report($"Installing patch {patch.Id}");
            using var http = new HttpClient();
            var target = Path.Combine(InstallRoot, string.IsNullOrEmpty(patch.Filename)
                ? Path.GetFileName(new Uri(patch.Url).AbsolutePath)
                : patch.Filename);
            using (var res = await http.GetAsync(patch.Url, ct))
            using (var fs = File.Create(target))
            {
                res.EnsureSuccessStatusCode();
                await res.Content.CopyToAsync(fs, ct);
            }

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

        progress?.Report("Downloading CrownLink");
        using var http = new HttpClient();
        using var res = await http.GetAsync(url, ct);
        using var fs = File.Create(CrownLinkSnp);
        res.EnsureSuccessStatusCode();
        await res.Content.CopyToAsync(fs, ct);
        _settings.CrownLinkAssetId = id;
        _settings.Save();
    }

    private async Task EnsureGameRepoAsync(string? commitSha, IProgress<string>? progress, CancellationToken ct)
    {
        using var repo = new RepoManager(RepoDir, _repo.Url);
        if (repo.IsInstalled())
        {
            var remoteName = repo.EnsureRemoteName();
            if (repo.HasLocalChanges())
            {
                progress?.Report("Local changes detected; launching as-is");
            }
            else
            {
                repo.Fetch(progress);
                var branch = string.IsNullOrWhiteSpace(_repo.Branch) ? "main" : _repo.Branch;
                if (commitSha is null)
                    repo.CheckoutBranch(branch, remoteName, progress);
                else
                    repo.ResetTo(commitSha, progress);
            }
        }
        else
        {
            if (Directory.Exists(RepoDir))
                Directory.Delete(RepoDir, recursive: true);
            repo.Clone(progress);
            if (commitSha is not null)
                repo.ResetTo(commitSha, progress);
        }
        await Task.CompletedTask; // git operations are sync in LibGit2Sharp
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
    private void FixWineRegistry(IProgress<string>? progress)    {
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

    private async Task LaunchAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo(GameExe)
            {
                WorkingDirectory = RepoDir,
                UseShellExecute = true,
            };
            Process.Start(psi);
            await WaitForStarcraftWindowAsync(ct);
            return;
        }

        // Linux: umu + GE-Proton in the shared prefix. Disable the handheld
        // panel when docked so Xwayland presents one clean 1080p screen.
        DisableHandheldPanelWhenDocked();

        var startInfo = new ProcessStartInfo("umu-run")
        {
            WorkingDirectory = RepoDir,
            UseShellExecute = false,
            ArgumentList =
            {
                $"C:\\Cosmonarchy\\{_repo.BuildDir ?? "Release"}\\Cosmonarchy BW.exe",
            },
        };
        startInfo.Environment["WINEPREFIX"] = Path.Combine(_settings.LinuxPrefixRoot, "prefix");
        startInfo.Environment["PROTONPATH"] = "GE-Proton";
        startInfo.Environment["GAMEID"] = $"umu-cosmonarchy-{_repo.BuildDir ?? "Release"}";
        startInfo.Environment["PROTON_VERB"] = "waitforexitandrun";
        Process.Start(startInfo);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static void DisableHandheldPanelWhenDocked()
    {
        try
        {
            var start = new ProcessStartInfo("hyprctl", "monitors -j")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(start);
            if (proc is null)
                return;
            var json = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (json.Contains("HDMI-A-1", StringComparison.Ordinal) &&
                !json.Contains("\"disabled\":true", StringComparison.Ordinal))
            {
                Process.Start(new ProcessStartInfo("hyprctl")
                {
                    UseShellExecute = false,
                    ArgumentList = { "eval", "hl.monitor({output='eDP-1', disabled=true})" },
                });
            }
        }
        catch
        {
            // Not Hyprland (or hyprctl missing): nothing to do.
        }
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
