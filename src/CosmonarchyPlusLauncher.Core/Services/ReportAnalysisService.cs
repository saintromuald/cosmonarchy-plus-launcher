using System.Text;
using Octokit;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>One section of an automated bug diagnosis.</summary>
public sealed record DiagnosisFinding(string Severity, string Title, string Detail);

/// <summary>The result of comparing two bug reports (or one report against
/// this machine).</summary>
public sealed class BugDiagnosis
{
    public string Headline { get; set; } = "";
    public List<DiagnosisFinding> Findings { get; } = [];

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Desync / crash diagnosis");
        sb.AppendLine();
        if (Headline.Length > 0)
        {
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine(Headline);
            sb.AppendLine();
        }
        sb.AppendLine("## Findings");
        foreach (var f in Findings)
        {
            sb.AppendLine();
            sb.AppendLine($"### [{f.Severity}] {f.Title}");
            sb.AppendLine();
            sb.AppendLine(f.Detail);
        }
        return sb.ToString();
    }
}

/// <summary>Pairs and compares bug reports: the two players' reports of the
/// same game carry the build identities, the configs that change the
/// simulation, the game's own desync records, and the crash signatures —
/// everything needed to say, mechanically, why a lockstep game diverged or
/// why a client crashed.</summary>
public sealed class ReportAnalysisService
{
    private readonly HttpClient _http;

    public ReportAnalysisService(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

    // ---- Public reports -----------------------------------------------------

    /// <summary>The slugs of every published report, newest first (public
    /// read needs no token).</summary>
    public async Task<List<string>> ListReportsAsync(string repo, CancellationToken ct = default)
    {
        var (owner, name) = Split(repo);
        var client = new GitHubClient(new ProductHeaderValue("CosmonarchyPlusLauncher"));
        var contents = await client.Repository.Content.GetAllContents(
            owner, name, CrashReportService.ReportsDirectory);
        return contents.Select(c => c.Name).OrderByDescending(n => n).ToList();
    }

    /// <summary>Download one report's files into a temp directory.</summary>
    public async Task<string> FetchReportAsync(string repo, string slug, CancellationToken ct = default)
    {
        var (owner, name) = Split(repo);
        var client = new GitHubClient(new ProductHeaderValue("CosmonarchyPlusLauncher"));
        var dir = Path.Combine(Path.GetTempPath(), "cosmo-report-" + slug);
        Directory.CreateDirectory(dir);
        var contents = await client.Repository.Content.GetAllContents(
            owner, name, $"{CrashReportService.ReportsDirectory}/{slug}");
        foreach (var file in contents)
        {
            var path = Path.Combine(dir, file.Name);
            if (file.Content is not null)
                await File.WriteAllTextAsync(path, file.Content, ct);
            else
                await using (var s = await _http.GetStreamAsync(file.DownloadUrl, ct))
                await using (var fs = File.Create(path))
                    await s.CopyToAsync(fs, ct);
        }
        return dir;
    }

    // ---- Comparison ---------------------------------------------------------

    /// <summary>Compare two collected report directories. The result names
    /// every mechanical difference that can desync a game or crash a client.</summary>
    public static BugDiagnosis Compare(string localDir, string remoteDir)
    {
        var d = new BugDiagnosis();
        var problems = 0;

        // Build identity.
        var localId = ReadFirst(localDir, "*_build-id.txt");
        var remoteId = ReadFirst(remoteDir, "*_build-id.txt");
        var localSha = Extract(localId, "sha:");
        var remoteSha = Extract(remoteId, "sha:");
        var localBranch = Extract(localId, "branch:");
        var remoteBranch = Extract(remoteId, "branch:");
        if (localSha.Length > 0 && remoteSha.Length > 0 && localSha != remoteSha)
        {
            problems++;
            d.Findings.Add(new("critical", "Different game builds",
                $"The two clients ran different builds of the game:\n\n" +
                $"  you:      {localSha} ({localBranch})\n" +
                $"  opponent: {remoteSha} ({remoteBranch})\n\n" +
                "Lockstep games desync whenever the builds differ. Both players must play the same build " +
                "(same repo, same branch, same commit)."));
        }

        // Plugin revisions.
        var localRev = ReadFirst(localDir, "*_cosmo_plus.log") is { } lp ? Marker(lp, "feature build:") : "";
        var remoteRev = ReadFirst(remoteDir, "*_cosmo_plus.log") is { } rp ? Marker(rp, "feature build:") : "";
        if (localRev.Length > 0 && remoteRev.Length > 0 && localRev != remoteRev)
        {
            problems++;
            d.Findings.Add(new("critical", "Different Plus plugin builds",
                $"The two clients ran different versions of the Plus plugin:\n\n" +
                $"  you:      {localRev}\n" +
                $"  opponent: {remoteRev}\n\n" +
                "The plugin changes the simulation, so the builds must match exactly. Update the older " +
                "side — in the launcher, select the build, press Update, and both players re-test."));
        }

        // Config files (the sim-changing ones).
        foreach (var name in new[] { "plus.yml", "conf_plus.yml", "conf_cosmo_plus.conf" })
        {
            var localConf = FindFile(localDir, name);
            var remoteConf = FindFile(remoteDir, name);
            if (localConf.Length == 0 || remoteConf.Length == 0)
                continue;
            if (Normalize(File.ReadAllText(localConf)) == Normalize(File.ReadAllText(remoteConf)))
                continue;
            var diff = LineDiff(File.ReadAllText(localConf), File.ReadAllText(remoteConf));
            problems++;
            d.Findings.Add(new("critical", $"{name} differs between the two clients",
                $"The two sides have different {name} — different cost/balance data diverges the " +
                $"simulation and desyncs every game. The differences:\n\n```\n{diff}\n```\n\n" +
                "Decide which side is correct and copy that file to the other machine " +
                "(Config tab → select the file → Save), then re-test."));
        }

        // Desync records.
        var localDesync = ReadFirst(localDir, "Critical*.log") ?? "";
        var remoteDesync = ReadFirst(remoteDir, "Critical*.log") ?? "";
        var frames = new List<string>();
        foreach (var text in new[] { localDesync, remoteDesync })
        {
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("Critical frame:", StringComparison.Ordinal))
                    frames.Add(line.Trim());
            }
        }
        if (frames.Count > 0)
        {
            d.Findings.Add(new("info", "The game detected the desync itself",
                $"The game's own desync detector fired: {string.Join("; ", frames)}. " +
                "The fix is the build/config alignment above — the desync is the symptom, not the cause."));
        }

        // Crash signatures.
        var localCrashes = CrashSignatures(localDir);
        var remoteCrashes = CrashSignatures(remoteDir);
        var shared = localCrashes.Intersect(remoteCrashes).ToList();
        if (shared.Count > 0)
        {
            d.Findings.Add(new("info", "Both sides hit the same crash",
                $"Both clients recorded the same crash signature: {string.Join(", ", shared)}. " +
                "A crash both sides share at the same address is an engine/module bug, not a setup " +
                "difference — include this signature when reporting."));
        }
        else if (remoteCrashes.Count > 0)
        {
            d.Findings.Add(new("info", "The opponent's crash signatures",
                $"The opponent's crash captures: {string.Join(", ", remoteCrashes)}."));
        }

        if (problems == 0 && frames.Count == 0)
        {
            d.Headline = "The two reports are consistent — no mechanical difference found. " +
                         "If the games still desync, compare the replays of the same game: the point " +
                         "where they diverge names the unit or order involved.";
        }
        else if (problems > 0)
        {
            d.Headline = $"{problems} mechanical differences found — fix them and the desyncs " +
                         "should stop. The findings below say exactly what to change on which machine.";
        }
        return d;
    }

    // ---- helpers ------------------------------------------------------------

    private static string ReadFirst(string dir, string pattern) =>
        Directory.GetFiles(dir, pattern).OrderBy(f => f).FirstOrDefault() is { } f
            ? File.ReadAllText(f)
            : "";

    private static string FindFile(string dir, string name) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).EndsWith(name, StringComparison.OrdinalIgnoreCase))
        ?? "";

    private static string Extract(string text, string key)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal));
        return line is null ? "" : line[key.Length..].Trim();
    }

    private static string Marker(string text, string marker)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Contains(marker, StringComparison.Ordinal));
        return line is null ? "" : line.Trim();
    }

    private static string Normalize(string text) =>
        string.Join("\n", text.Split('\n').Select(l => l.Trim()));

    /// <summary>A compact line-level diff (only differing lines, ±context).</summary>
    private static string LineDiff(string a, string b)
    {
        var al = a.Split('\n');
        var bl = b.Split('\n');
        var sb = new StringBuilder();
        var n = Math.Max(al.Length, bl.Length);
        var shown = 0;
        for (var i = 0; i < n && shown < 24; i++)
        {
            var x = i < al.Length ? al[i].Trim() : "(missing)";
            var y = i < bl.Length ? bl[i].Trim() : "(missing)";
            if (x == y)
                continue;
            sb.AppendLine($"  you:      {x}");
            sb.AppendLine($"  opponent: {y}");
            shown += 2;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Crash addresses recorded in a report's crash captures.</summary>
    private static List<string> CrashSignatures(string dir)
    {
        var found = new List<string>();
        foreach (var f in Directory.GetFiles(dir, "*cosmo_crash.log"))
        {
            foreach (var line in File.ReadAllLines(f))
            {
                var i = line.IndexOf("addr=", StringComparison.Ordinal);
                if (i >= 0 && line.Length > i + 5)
                    found.Add(line[(i + 5)..].Split(' ')[0]);
            }
        }
        return found.Distinct().ToList();
    }

    private static (string Owner, string Name) Split(string repo)
    {
        var parts = repo.Trim().Trim('/').Split('/');
        if (parts.Length < 2)
            throw new InvalidOperationException($"Cannot read owner/name from “{repo}”.");
        return (parts[0], parts[1]);
    }
}
