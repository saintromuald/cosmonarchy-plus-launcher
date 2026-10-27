using System.Text;
using CosmonarchyPlusLauncher.Core.Settings;
using Octokit;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Packages the most recent game logs and crash captures into a
/// GitHub-hosted report, so a bug can be pointed at with a link.
///
/// <para>The report is a folder under <c>crash-reports/&lt;timestamp&gt;/</c>
/// in the community repo (the same repo the Community tab publishes to):
/// a <c>summary.md</c> with the crash signatures and build identity, plus the
/// capped tails of the plugin logs, the game's own Critical logs and .ERR,
/// the boot state report, the launcher diagnostics, and the config files
/// that change the simulation (differences there desync lockstep games).
/// Publishing needs the same GitHub token the community publishing uses.</para></summary>
public sealed class CrashReportService
{
    public const string ReportsDirectory = "crash-reports";

    /// <summary>Cap each collected log so a report never balloons.</summary>
    private const long MaxFileBytes = 200_000;
    /// <summary>Replays are binary and can be larger; cap them higher.</summary>
    private const long MaxReplayBytes = 2_000_000;

    /// <summary>Collect the recent diagnostics into a fresh temp directory.
    /// Returns the directory path. <paramref name="note"/> is the player's
    /// own description of the bug and opens the report summary.</summary>
    public static Task<string> CollectAsync(
        AppSettings settings, string note = "",
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var root = settings.RootPath;
            var outDir = Path.Combine(Path.GetTempPath(),
                "cosmo-bug-report-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(outDir);

            foreach (var dir in BuildDirs(root))
            {
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                foreach (var f in new[]
                {
                    "cosmo_plus.log", "cosmo_crash.log", "cosmo_rollback.log",
                    "cosmo_plus.state.json", "hotloader.log", "IsDbg.log",
                })
                {
                    ct.ThrowIfCancellationRequested();
                    CopyCapped(Path.Combine(dir, f), Path.Combine(outDir, $"{name}_{f}"));
                }

                var logsDir = Path.Combine(dir, "logs");
                if (Directory.Exists(logsDir))
                {
                    foreach (var c in Directory.GetFiles(logsDir, "Critical*.log")
                                 .OrderByDescending(File.GetLastWriteTimeUtc).Take(2))
                        CopyCapped(c, Path.Combine(outDir, $"{name}_{Path.GetFileName(c)}"));
                }

                var id = BuildIdentity(dir, name);
                if (id.Length > 0)
                    File.WriteAllText(Path.Combine(outDir, $"{name}_build-id.txt"), id);
            }

            progress?.Report("Collecting the game's own crash logs…");
            var errors = Path.Combine(root, "Starcraft", "Errors");
            if (Directory.Exists(errors))
            {
                foreach (var e in Directory.GetFiles(errors, "*.ERR")
                             .OrderByDescending(File.GetLastWriteTimeUtc).Take(2))
                    CopyCapped(e, Path.Combine(outDir, Path.GetFileName(e)));
            }

            progress?.Report("Collecting configs (differences here desync games)…");
            foreach (var p in new[]
            {
                Path.Combine(root, "Starcraft", "cosmo_plus.conf"),
                Path.Combine(root, "Starcraft", "plus.yml"),
            })
                CopyCapped(p, Path.Combine(outDir, "conf_" + Path.GetFileName(p)));

            foreach (var dir in BuildDirs(root))
            {
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                foreach (var p in new[] { "plus.yml", Path.Combine("plugins", "plus.yml") })
                    CopyCapped(Path.Combine(dir, p), Path.Combine(outDir, $"{name}_conf_{Path.GetFileName(p)}"));
            }

            var launcherLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cosmonarchy-plus-launcher", "launcher.log");
            CopyCapped(launcherLog, Path.Combine(outDir, "launcher.log"));

            progress?.Report("Collecting the latest replays…");
            foreach (var replayDir in new[]
            {
                Path.Combine(root, "Starcraft", "maps", "replays"),
                Path.Combine(root, "Starcraft", "Maps", "replays"),
            })
            {
                if (!Directory.Exists(replayDir))
                    continue;
                foreach (var r in Directory.GetFiles(replayDir, "*.rep")
                             .OrderByDescending(File.GetLastWriteTimeUtc).Take(4))
                    CopyCapped(r, Path.Combine(outDir, Path.GetFileName(r)), MaxReplayBytes);
            }

            progress?.Report("Writing the report summary…");
            File.WriteAllText(Path.Combine(outDir, "summary.md"), BuildSummary(outDir, note));
            return outDir;
        }, ct);
    }

    /// <summary>Publish a collected report directory to the community repo.
    /// Returns the URL of the report's summary.</summary>
    public static async Task<string> PublishAsync(
        string repo, string token, string reportDir, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var (owner, name) = Split(repo);
        var client = new GitHubClient(new ProductHeaderValue("CosmonarchyPlusLauncher"));
        if (!string.IsNullOrWhiteSpace(token))
            client.Credentials = new Credentials(token);

        var slug = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var basePath = $"{ReportsDirectory}/{slug}";
        var files = Directory.GetFiles(reportDir).OrderBy(f => f).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            progress?.Report($"Publishing {i + 1}/{files.Count}: {Path.GetFileName(file)}…");
            var path = $"{basePath}/{Path.GetFileName(file)}";
            try
            {
                await client.Repository.Content.CreateFile(owner, name, path,
                    new CreateFileRequest($"Crash report {slug}", Encode(file)));
            }
            catch (NotFoundException)
            {
                throw new InvalidOperationException(
                    $"The community repo “{repo}” does not exist yet — create it as a public GitHub repo.");
            }
            catch (AuthorizationException)
            {
                throw new InvalidOperationException(
                    "GitHub rejected the token. In Settings, add a fine-grained token with “Contents: read and write” on the community repo (or a classic token with repo scope).");
            }
            catch (ForbiddenException)
            {
                throw new InvalidOperationException(
                    "GitHub refused the upload — check that the token can write to the community repo.");
            }
        }

        Directory.Delete(reportDir, recursive: true);
        return $"https://github.com/{owner}/{name}/blob/main/{basePath}/summary.md";
    }

    // ---- Collection helpers -------------------------------------------------

    private static IEnumerable<string> BuildDirs(string root)
    {
        var versions = Path.Combine(root, "versions");
        if (Directory.Exists(versions))
        {
            foreach (var d in Directory.GetDirectories(versions))
                yield return d;
        }
        foreach (var d in new[] { "Release", "Prerelease", "Original" })
        {
            var full = Path.Combine(root, d);
            if (Directory.Exists(full))
                yield return full;
        }
    }

    private static void CopyCapped(string source, string destination, long cap = MaxFileBytes)
    {
        if (!File.Exists(source))
            return;
        var bytes = File.ReadAllBytes(source);
        if (bytes.Length > cap)
        {
            // Keep the tail: the newest lines are the interesting ones.
            var tail = bytes.AsSpan(bytes.Length - (int)cap);
            var head = Encoding.UTF8.GetBytes($"[truncated: {bytes.Length - tail.Length} bytes omitted]\n");
            using var fs = File.Create(destination);
            fs.Write(head);
            fs.Write(tail);
            return;
        }
        File.Copy(source, destination, overwrite: true);
    }

    private static string BuildIdentity(string dir, string name)
    {
        var gitDir = Path.Combine(dir, ".git");
        if (!Directory.Exists(gitDir))
            return "";
        var head = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(head))
            return "";
        var headText = File.ReadAllText(head).Trim();
        string sha = "";
        string branch = "detached";
        if (headText.StartsWith("ref:", StringComparison.Ordinal))
        {
            branch = headText[5..].Trim();
            var refFile = Path.Combine(gitDir, branch.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refFile))
                sha = File.ReadAllText(refFile).Trim();
        }
        else
        {
            sha = headText;
        }
        return $"build: {name}\nbranch: {branch}\nsha: {sha}";
    }

    /// <summary>A short markdown summary: the player's note, build identity,
    /// plugin revisions, crash signatures, and pointers at the rest of the
    /// files.</summary>
    private static string BuildSummary(string dir, string note)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cosmonarchy bug report");
        sb.AppendLine();
        sb.AppendLine($"- Collected: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        if (!string.IsNullOrWhiteSpace(note))
        {
            sb.AppendLine();
            sb.AppendLine("## What happened");
            sb.AppendLine();
            sb.AppendLine(note.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("## Builds");
        foreach (var id in Directory.GetFiles(dir, "*_build-id.txt").OrderBy(f => f))
        {
            var text = File.ReadAllText(id).Trim();
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(text);
            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine("## Plugin builds");
        foreach (var q in Directory.GetFiles(dir, "*_cosmo_plus.log").OrderBy(f => f))
        {
            var rev = ScanForMarker(q, "feature build:");
            if (rev.Length > 0)
                sb.AppendLine($"- `{Path.GetFileName(q)}`: {rev}");
        }

        sb.AppendLine();
        sb.AppendLine("## Crash signatures");
        var crashes = new List<string>();
        foreach (var c in Directory.GetFiles(dir, "*_cosmo_crash.log").OrderBy(f => f))
        {
            foreach (var line in File.ReadAllLines(c))
            {
                if (line.Contains("exception=", StringComparison.Ordinal)
                    || line.Contains("tick=", StringComparison.Ordinal)
                    || line.Contains("[cosmo-crash]", StringComparison.Ordinal))
                    crashes.Add($"`{Path.GetFileName(c)}` — {line.Trim()}");
            }
        }
        if (crashes.Count == 0)
            sb.AppendLine("(no crash captures recorded)");
        else
        {
            foreach (var c in crashes.TakeLast(40))
                sb.AppendLine($"- {c}");
        }

        sb.AppendLine();
        sb.AppendLine("## Files");
        foreach (var f in Directory.GetFiles(dir).OrderBy(f => f))
        {
            if (Path.GetFileName(f) == "summary.md")
                continue;
            sb.AppendLine($"- [{Path.GetFileName(f)}]({Path.GetFileName(f)})");
        }
        return sb.ToString();
    }

    /// <summary>Find an ascii marker in a (possibly binary) file.</summary>
    private static string ScanForMarker(string path, string marker)
    {
        if (!File.Exists(path))
            return "";
        var data = File.ReadAllBytes(path);
        var needle = Encoding.ASCII.GetBytes(marker);
        for (var i = 0; i + needle.Length <= data.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (data[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (!match)
                continue;
            var end = i + needle.Length;
            while (end < data.Length && data[end] is >= 0x20 and < 0x7F)
                end++;
            return Encoding.ASCII.GetString(data, i, end - i);
        }
        return "";
    }

    private static string Encode(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // GitHub's API needs text; the collected files are logs/text, so a
        // straight UTF-8 decode is safe.
        return Encoding.UTF8.GetString(bytes);
    }

    private static (string Owner, string Name) Split(string repo)
    {
        var parts = repo.Trim().Trim('/').Split('/');
        if (parts.Length < 2)
            throw new InvalidOperationException($"Cannot read owner/name from “{repo}”.");
        return (parts[0], parts[1]);
    }
}
