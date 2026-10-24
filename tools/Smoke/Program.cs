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
                "--mpq" => MpqAsync(),
                "--matches" => await MatchesAsync(args.Skip(1).ToArray()),
                "--resolve" => await ResolveBuildAsync(args.Skip(1).ToArray()),
                "--watch" => await WatchMatchAsync(args.Skip(1).ToArray()),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SMOKE FAILED: {ex}");
            return 1;
        }
    }

    private static int MpqAsync()
    {
        var build = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Games", "cosmonarchy", "prefix", "drive_c", "Cosmonarchy", "Release");
        var mpq = Path.Combine(build, "temp.mpq");
        var tree = Path.Combine(build, "mpq");
        if (!File.Exists(mpq))
        {
            Console.WriteLine("temp.mpq not present");
            return 0;
        }
        var probe = TempMpqChecker.ProbeArchive(mpq, tree);
        Console.WriteLine($"probe: header={probe.HeaderOk} hashEntries={probe.HashEntries} blockEntries={probe.BlockEntries} missing={probe.Missing} treeFiles={probe.TreeFiles}");
        Console.WriteLine($"fail: {probe.Fail.Replace("\n", " | ")}");
        Console.WriteLine($"first missing: {probe.FirstMissing}");
        Console.WriteLine($"hash keys: ht={TempMpqChecker.HashTableKey:X8} bt={TempMpqChecker.BlockTableKey:X8}");
        Console.WriteLine($"first entry: {TempMpqChecker.ProbeFirstEntry(mpq)}");
        var ok = TempMpqChecker.IsComplete(mpq, tree);
        Console.WriteLine($"temp.mpq complete: {ok}");
        return ok ? 0 : 1;

    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: Smoke [--live|--tmp|--mpq|--matches [player]|--resolve <sha> [kind]]");
        return 2;
    }

    /// <summary>Full watch pipeline for one online match (optionally without
    /// launching, to verify the build/download/staging part):
    /// <c>Smoke --watch &lt;matchUuid&gt; [--no-launch]</c>.</summary>
    private static async Task<int> WatchMatchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: Smoke --watch <matchUuid> [--no-launch]");
            return 2;
        }
        var launch = !args.Contains("--no-launch");
        var settings = AppSettings.Load();
        var kraken = new ReplayKrakenService();
        var all = await kraken.GetMatchesAsync();
        var match = all.FirstOrDefault(m => m.MatchUuid.Equals(args[0], StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Match {args[0]} not in the current list.");
        Console.WriteLine($"match: {match.PlayersLabel} on {match.MapName}, {match.VersionLabel}");
        var progress = new Progress<string>(s => Console.WriteLine($"  {s}"));
        var service = new ReplayWatchService(settings, kraken);
        var build = await service.WatchAsync(match, progress, launch: launch);
        Console.WriteLine($"channel={build.Channel} dir={build.Dir} sha={build.FullSha[..8]} replay={match.FileName}");
        Console.WriteLine(launch ? "WATCH SMOKE OK" : "WATCH PREPARE SMOKE OK");
        return 0;
    }

    /// <summary>Resolve a replay's short SHA to a build and check it out, the
    /// way one-click watching does (no download, no launch).
    /// <c>Smoke --resolve &lt;sha&gt; [Release|Prerelease]</c>.</summary>
    private static async Task<int> ResolveBuildAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: Smoke --resolve <sha> [Release|Prerelease]");
            return 2;
        }
        var sha = args[0];
        var kind = args.Length > 1 ? args[1] : "Release";
        var settings = AppSettings.Load();
        var match = new OnlineMatch(
            "smoke", DateTimeOffset.UtcNow, "Smoke", TimeSpan.Zero, sha, kind, [], "smoke", "smoke");
        var progress = new Progress<string>(s => Console.WriteLine($"  {s}"));
        var build = await new ReplayBuildResolver(settings).ResolveAsync(match, progress);
        Console.WriteLine($"channel={build.Channel} repo={build.Repo.Id} dir={build.Dir} sha={build.FullSha}");

        using var repo = new RepoManager(Path.Combine(settings.RootPath, build.Dir), build.Repo.Url);
        repo.CheckoutCommit(build.FullSha, progress);
        Console.WriteLine($"head={repo.NewestCommit()?.Sha} ({repo.NewestCommit()?.Message})");
        Console.WriteLine("RESOLVE SMOKE OK");
        return 0;
    }

    private static IProgress<string> Reporter(string tag) =>
        new Progress<string>(s => Console.WriteLine($"[{tag}] {s}"));

    /// <summary>Live check of the ReplayKraken list (the Replays page's
    /// "Online games" tab): fetch, map, cache, then show the latest 20 and
    /// optionally one player's games.</summary>
    private static async Task<int> MatchesAsync(string[] args)
    {
        var service = new ReplayKrakenService();
        var progress = new Progress<string>(s => Console.WriteLine(s));
        var all = await service.GetMatchesAsync(force: true, progress);
        Console.WriteLine($"total matches: {all.Count}");
        var latest = ReplayKrakenService.Filter(all, 20);
        Console.WriteLine($"latest {latest.Count}:");
        foreach (var m in latest.Take(5))
            Console.WriteLine($"  {m.DateLabel}  {m.PlayersLabel}  {m.MapName}  {m.DurationLabel}  {m.VersionLabel}");
        if (args.Length > 0)
        {
            var player = args[0];
            var theirs = ReplayKrakenService.Filter(all, 100, player);
            Console.WriteLine($"{theirs.Count} game(s) for {player}:");
            foreach (var m in theirs.Take(5))
                Console.WriteLine($"  {m.DateLabel}  {m.PlayersLabel}  {m.MapName}  {m.VersionLabel}");
        }
        Console.WriteLine("MATCHES SMOKE OK");
        return 0;
    }

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

        ListVersions(installRoot);

        Console.WriteLine("LIVE SMOKE OK");
        return 0;
    }

    /// <summary>Read-only listing of the versions the launcher would adopt.
    /// Uses throwaway settings so the user's settings file is never written.</summary>
    private static void ListVersions(string installRoot)
    {
        var throwaway = Path.Combine(Path.GetTempPath(), $"cpl-live-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings { FilePath = throwaway, RootPath = installRoot };
            var manager = new VersionManager(settings);
            var adopted = manager.AdoptExistingInstallations();
            Console.WriteLine($"versions     : adopted {adopted} checkout(s)");
            foreach (var v in manager.Versions)
                Console.WriteLine(
                    $"  - {v.Id,-12} {v.Name,-30} dir={v.Dir,-26} repo={v.RepoId,-10} " +
                    $"{(v.TracksBranch ? "tracks branch" : "pinned " + v.PinnedSha?[..8])}  now {v.InstalledSha?[..8]}");
        }
        finally
        {
            File.Delete(throwaway);
        }
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
            RunGit(gameSrc, "init", "-q", "-b", "main");
            Directory.CreateDirectory(Path.Combine(gameSrc, "plugins"));
            File.WriteAllText(Path.Combine(gameSrc, "Cosmonarchy BW.exe"), "placeholder");
            RunGit(gameSrc, "add", ".");
            RunGit(gameSrc, "-c", "user.email=smoke@test", "-c", "user.name=Smoke", "commit", "-q", "-m", "initial build");
            string commitA;
            using (var seed = new LibGit2Sharp.Repository(gameSrc))
                commitA = seed.Head.Tip.Sha;

            // Local maps repo.
            Directory.CreateDirectory(mapsSrc);
            RunGit(mapsSrc, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(mapsSrc, "sample.map"), "map");
            RunGit(mapsSrc, "add", ".");
            RunGit(mapsSrc, "-c", "user.email=smoke@test", "-c", "user.name=Smoke", "commit", "-q", "-m", "maps");

            // Fake StarCraft base so the big download is skipped.
            Directory.CreateDirectory(Path.Combine(installRoot, "Starcraft"));
            File.WriteAllText(Path.Combine(installRoot, "Starcraft", "StarCraft.exe"), "fake");

            var settings = new AppSettings
            {
                // Keep the throwaway run out of the user's real settings file:
                // InstallService.Save() writes back to whatever file the
                // settings came from, and the default would be ~/.config.
                FilePath = Path.Combine(tmp, "settings.json"),
                RootPath = installRoot,
                LinuxPrefixRoot = prefixRoot,
            };
            var entry = new RepoEntry("release-smoke", "Release (Smoke)", gameSrc, RepoKind.Game, BuildDir: "Release");
            var v1 = new GameVersion { Id = "release", Name = "Release (Smoke)", RepoId = "release-smoke", Dir = "Release" };
            var service = new InstallService(settings, entry, v1, mapsUrl: mapsSrc);

            await service.InstallAsync(Reporter("install"));

            // Push a second build, then install it as a NEW pinned version:
            // both versions must coexist, each in its own checkout.
            var manager = new VersionManager(settings, [entry]);
            var adopted = manager.AdoptExistingInstallations(known: [entry]);
            File.WriteAllText(Path.Combine(gameSrc, "Cosmonarchy BW.exe"), "placeholder v2");
            RunGit(gameSrc, "add", ".");
            RunGit(gameSrc, "-c", "user.email=smoke@test", "-c", "user.name=Smoke", "commit", "-q", "-m", "second build");

            await manager.InstallAsync(entry, commitA, "Old build", Reporter("install2"), mapsUrl: mapsSrc);
            var adoptedVersion = manager.Find("release")!;
            await manager.UpdateAsync(adoptedVersion, Reporter("update"), mapsUrl: mapsSrc);

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

            var oldBuildExe = Path.Combine(installRoot, "versions", "old-build", "Cosmonarchy BW.exe");
            Check("adopted legacy checkout", adopted == 1);
            Check("second version cloned", File.Exists(oldBuildExe));
            Check("pinned version frozen", File.Exists(oldBuildExe)
                && File.ReadAllText(oldBuildExe) == "placeholder");
            Check("tracked version updated",
                File.ReadAllText(Path.Combine(installRoot, "Release", "Cosmonarchy BW.exe")) == "placeholder v2");
            Check("version list has both", settings.Versions.Count == 2);
            Check("new version selected", settings.SelectedVersionId == "old-build");

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
