using System.Diagnostics;
using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Smoke;

/// <summary>Two smoke tests for the Core pipeline:
///   --live  fetch/log the real ~/Games/cosmonarchy prefix (no reset, no launch)
///   --tmp   run the whole install pipeline into a throwaway directory with
///           local git repos (real network only for patches + CrownLink)</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "--tmp";
        try
        {
            return mode switch
            {
                "--live" => await LiveAsync(),
                "--tmp" => await TempAsync(),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SMOKE FAILED: {ex}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: Smoke [--live|--tmp]");
        return 2;
    }

    private static IProgress<string> Reporter(string tag) =>
        new Progress<string>(s => Console.WriteLine($"[{tag}] {s}"));

    private static async Task<int> LiveAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var installRoot = Path.Combine(home, "Games", "cosmonarchy", "prefix", "drive_c", "Cosmonarchy");
        var repoDir = Path.Combine(installRoot, "Release");
        var mapsLower = Path.Combine(installRoot, "Starcraft", "maps", "CMBW-Root");
        var mapsDir = Directory.Exists(mapsLower)
            ? mapsLower
            : Path.Combine(installRoot, "Starcraft", "Maps", "CMBW-Root");
        var starcraftExe = Path.Combine(installRoot, "Starcraft", "StarCraft.exe");
        var crownLink = Path.Combine(installRoot, "Starcraft", "CrownLink.snp");

        Console.WriteLine($"install root : {installRoot}");
        Console.WriteLine($"starcraft    : {(File.Exists(starcraftExe) ? "present" : "MISSING")}");
        Console.WriteLine($"crownlink    : {(File.Exists(crownLink) ? "present" : "missing")}");

        var entry = RepoCatalog.Find(AppSettings.DefaultRepoId)!;
        using (var repo = new RepoManager(repoDir, entry.Url))
        {
            Console.WriteLine($"game repo    : installed={repo.IsInstalled()}");
            if (repo.IsInstalled())
            {
                repo.Fetch(Reporter("game"));
                var newest = repo.NewestCommit();
                Console.WriteLine($"game HEAD    : {newest?.Sha[..8]} {newest?.Message}");
                Console.WriteLine($"game log     : {repo.Log().Count} commits");
                Console.WriteLine($"game dirty   : {repo.HasLocalChanges()}");
            }
        }

        using (var maps = new RepoManager(mapsDir, RepoCatalog.Maps.Url))
        {
            Console.WriteLine($"maps repo    : installed={maps.IsInstalled()}");
            if (maps.IsInstalled())
            {
                maps.Fetch(Reporter("maps"));
                var newest = maps.NewestCommit();
                Console.WriteLine($"maps HEAD    : {newest?.Sha[..8]} {newest?.Message}");
            }
        }

        Console.WriteLine("LIVE SMOKE OK");
        return 0;
    }

    private static async Task<int> TempAsync()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cpl-smoke-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(tmp, "install");
        var prefixRoot = Path.Combine(tmp, "prefix");
        var gameSrc = Path.Combine(tmp, "game-src");
        var mapsSrc = Path.Combine(tmp, "maps-src");
        Directory.CreateDirectory(tmp);

        try
        {
            // Local "release" repo with the files the game build must have.
            Directory.CreateDirectory(gameSrc);
            RunGit(gameSrc, "init", "-q");
            Directory.CreateDirectory(Path.Combine(gameSrc, "plugins"));
            File.WriteAllText(Path.Combine(gameSrc, "Cosmonarchy BW.exe"), "placeholder");
            RunGit(gameSrc, "add", ".");
            RunGit(gameSrc, "-c", "user.email=smoke@test", "-c", "user.name=Smoke", "commit", "-q", "-m", "initial build");

            // Local maps repo.
            Directory.CreateDirectory(mapsSrc);
            RunGit(mapsSrc, "init", "-q");
            File.WriteAllText(Path.Combine(mapsSrc, "sample.map"), "map");
            RunGit(mapsSrc, "add", ".");
            RunGit(mapsSrc, "-c", "user.email=smoke@test", "-c", "user.name=Smoke", "commit", "-q", "-m", "maps");

            // Fake StarCraft base so the big download is skipped.
            Directory.CreateDirectory(Path.Combine(installRoot, "Starcraft"));
            File.WriteAllText(Path.Combine(installRoot, "Starcraft", "StarCraft.exe"), "fake");

            var settings = new AppSettings
            {
                RootPath = installRoot,
                LinuxPrefixRoot = prefixRoot,
            };
            var entry = new RepoEntry("release-smoke", "Release (Smoke)", gameSrc, RepoKind.Game, BuildDir: "Release");
            var service = new InstallService(settings, entry, mapsUrl: mapsSrc);

            await service.InstallAsync(null, Reporter("install"));

            var ok = true;
            void Check(string label, bool cond)
            {
                Console.WriteLine($"{label,-26} {(cond ? "ok" : "FAILED")}");
                ok &= cond;
            }

            Check("game repo cloned", File.Exists(Path.Combine(installRoot, "Release", "Cosmonarchy BW.exe")));
            Check("maps cloned", File.Exists(Path.Combine(service.MapsDir, "sample.map")));
            Check("crownlink downloaded", File.Exists(Path.Combine(installRoot, "Starcraft", "CrownLink.snp")));
            Check("patches applied", (settings.PatchesInstalled?.Count ?? 0) > 0);

            Console.WriteLine(ok ? "TEMP SMOKE OK" : "TEMP SMOKE FAILED");
            return ok ? 0 : 1;
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    private static void RunGit(string workDir, params string[] gitArgs)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in gitArgs)
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', gitArgs)} failed: {proc.StandardError.ReadToEnd()}");
    }
}
