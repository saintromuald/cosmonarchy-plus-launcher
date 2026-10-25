using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>One-click replay watching: work out the build, download the replay
/// into the game's replay folder, switch the replay store to that build, and
/// launch straight into playback.</summary>
public sealed class ReplayWatchService
{
    private readonly AppSettings _settings;
    private readonly ReplayKrakenService _kraken;

    public ReplayWatchService(AppSettings settings, ReplayKrakenService? kraken = null)
    {
        _settings = settings;
        _kraken = kraken ?? new ReplayKrakenService();
    }

    public ReplayKrakenService Kraken => _kraken;

    /// <summary>The result of a watch launch: the build that played it and the
    /// menu-friendly staged file name (sorts last in the game's replay list).</summary>
    public sealed record WatchResult(ReplayBuild Build, string StagedFileName);

    public async Task<WatchResult> WatchAsync(
        OnlineMatch match, IProgress<string>? progress = null, CancellationToken ct = default,
        bool launch = true)
    {
        if (launch && GameIsRunning())
            throw new InvalidOperationException(
                "Cosmonarchy is already running. Close it, then click Watch again.");

        var build = await new ReplayBuildResolver(_settings).ResolveAsync(match, progress, ct);
        var version = new GameVersion
        {
            Id = $"replay-{build.FullSha[..8]}",
            Name = $"{build.Label} {build.FullSha[..8]}",
            RepoId = build.Repo.Id,
            Dir = build.Dir,
            PinnedSha = build.FullSha,
        };
        var service = new InstallService(_settings, build.Repo, version);

        progress?.Report("Downloading the replay…");
        var replay = await _kraken.DownloadReplayAsync(match, service.ReplayGameDir, progress, ct);

        // Stage a menu-friendly copy that sorts LAST in the game's replay
        // list, so WatchMenuDriver can open it with End + Enter.
        var staged = WatchMenuDriver.WatchFileName(match.MatchUuid);
        var stagedPath = Path.Combine(service.ReplayGameDir, staged);
        Directory.CreateDirectory(service.ReplayGameDir);
        if (!File.Exists(stagedPath))
            File.Copy(replay, stagedPath, overwrite: true);

        await service.PlayReplayAsync(replay, progress, ct, forceCheckout: true, launch: launch);
        return new WatchResult(build, staged);
    }

    private static bool GameIsRunning()
    {
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (name.Contains("Cosmonarchy BW", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Starcraft", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // Access denied for some system processes; ignore them.
            }
        }
        return false;
    }
}
