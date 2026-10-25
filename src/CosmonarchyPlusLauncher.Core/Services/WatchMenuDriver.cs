using System.Diagnostics;
using System.Text.Json;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Drives the game's menus with synthetic keystrokes so a replay
/// starts playing with zero clicks. The mod launcher ("Cosmonarchy BW.exe")
/// drops command-line arguments (verified), so the replay cannot be passed
/// on the command line; instead the replay is pre-staged in the game's
/// replay folder under a name that sorts LAST, and this driver walks:
/// Single Player → (profile dialog: Ok) → Watch Replay → last file → open.
///
/// Linux/Hyprland only: the game window is focused by address through
/// <c>hl.dsp.focus</c>, then keys are typed with <c>wtype</c>. Every step is
/// photographed (grim + tesseract) into the launcher data directory so a
/// missed step can be diagnosed after the fact.</summary>
public sealed class WatchMenuDriver
{
    /// <summary>Prefix that sorts after every ordinary replay name, so the
    /// staged replay is always the last entry of the in-game list.</summary>
    public const string WatchPrefix = "zzz-watch";

    /// <summary>The staged file name for a match (the UUID keeps consecutive
    /// watches from clobbering each other's file).</summary>
    public static string WatchFileName(string matchUuid) =>
        $"{WatchPrefix}-{matchUuid[..8]}.rep";

    public static async Task DriveAsync(
        string replayFileName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var address = await WaitForGameWindowAsync(ct);
        if (address is null)
        {
            progress?.Report("The game window did not appear; open Single Player → Watch Replay to watch.");
            return;
        }

        progress?.Report("Opening the replay in-game (no clicks needed)…");
        await PressAsync(address, "s", ct, 2200, 3000, "main-menu-single-player");
        await PressAsync(address, "Return", ct, 600, 3000, "profile-dialog-ok");
        await PressAsync(address, "w", ct, 900, 3000, "single-player-watch-replay");
        await PressAsync(address, "End", ct, 400, 800, "replay-list-last-file");
        await PressAsync(address, "Return", ct, 400, 800, "replay-list-open");
        progress?.Report($"Watching {replayFileName}.");
    }

    /// <summary>Refocus the game window, send the key twice (menu transitions
    /// sometimes eat the first press), and photograph the screen afterwards.</summary>
    private static async Task PressAsync(
        string address, string key, CancellationToken ct, int betweenMs, int afterMs, string step)
    {
        Focus(address);
        await Task.Delay(betweenMs, ct);
        Type(key);
        await Task.Delay(350, ct);
        Type(key);
        await Task.Delay(afterMs, ct);
        Snapshot(step);
    }

    /// <summary>grim + tesseract: photograph the screen and OCR it into the
    /// launcher data directory so the step's outcome can be checked later.</summary>
    private static void Snapshot(string step)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cosmonarchy-plus-launcher", "watch-steps");
            Directory.CreateDirectory(dir);
            var png = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{step}.png");
            var txt = png + ".txt";
            RunSilent("/usr/bin/grim", "-t", "png", png);
            if (!File.Exists(png))
                return;
            RunSilent("/usr/bin/tesseract", png, txt[..^4]); // writes <base>.txt
        }
        catch
        {
            // Diagnosis aid only; never fail the watch over it.
        }
    }

    private static void RunSilent(string file, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
                info.ArgumentList.Add(a);
            using var proc = Process.Start(info);
            proc?.WaitForExit(3000);
        }
        catch
        {
            // Optional tooling (grim/tesseract) missing: skip the snapshot.
        }
    }

    /// <summary>Polls hyprctl clients for the game window and returns its
    /// address, or null after ~90 seconds.</summary>
    private static async Task<string?> WaitForGameWindowAsync(CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            var address = GameWindowAddress();
            if (address is not null)
                return address;
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return null;
    }

    private static string? GameWindowAddress()
    {
        try
        {
            var info = new ProcessStartInfo("/usr/bin/hyprctl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "clients", "-j" },
            };
            using var proc = Process.Start(info);
            if (proc is null)
                return null;
            var json = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            using var doc = JsonDocument.Parse(json);
            foreach (var client in doc.RootElement.EnumerateArray())
            {
                var cls = client.TryGetProperty("class", out var c) ? c.GetString() : "";
                if (cls is not null &&
                    (cls.Contains("steam_app", StringComparison.OrdinalIgnoreCase) ||
                     cls.Contains("Cosmonarchy", StringComparison.OrdinalIgnoreCase)))
                {
                    return client.TryGetProperty("address", out var a) ? a.GetString() : null;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            // hyprctl missing or output unparseable: no window automation.
        }
        return null;
    }

    private static void Focus(string address)
    {
        try
        {
            var info = new ProcessStartInfo("/usr/bin/hyprctl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("dispatch");
            info.ArgumentList.Add($"hl.dsp.focus({{ window = \"address:{address}\" }})");
            using var proc = Process.Start(info);
            proc?.WaitForExit(2000);
        }
        catch
        {
            // Best effort: if the focus fails the keys simply do not land.
        }
    }

    private static void Type(string key)
    {
        try
        {
            var info = new ProcessStartInfo("/usr/bin/wtype")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-k", key },
            };
            using var proc = Process.Start(info);
            proc?.WaitForExit(1500);
        }
        catch
        {
            // wtype missing: the game stays on its menu; the replay is staged.
        }
    }
}
