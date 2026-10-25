using CosmonarchyPlusLauncher.Core.Settings;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Append-only launcher log for failures the UI can only summarise
/// (a failed clone, a missing plugin artifact, a broken checkout). The Play
/// page shows the message; the log keeps the stack trace so a bug report has
/// something to read.</summary>
public static class DiagnosticsLog
{
    private static readonly object Gate = new();

    /// <summary>Override for tests; null = the per-user data directory.</summary>
    public static string? PathOverride { get; set; }

    public static string FilePath =>
        PathOverride ?? Path.Combine(PlatformDefaults.DataDir(), "launcher.log");

    public static void Info(string message) => Append($"INFO {message}");

    public static void Error(string context, Exception exception) =>
        Append($"ERROR {context}: {exception}");

    private static void Append(string text)
    {
        lock (Gate)
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never turn a recoverable failure into a crash.
            }
        }
    }
}
