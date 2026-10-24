using System.Diagnostics;
using System.Net.Http;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Download helpers that report throttled byte progress, so the UI
/// can show "Downloading StarCraft… 43% (512 MB / 1.2 GB)" instead of a
/// status that never moves. Messages always carry a percentage when the
/// server provides a content length; callers show the text and can also feed
/// the number into a progress bar via <see cref="PercentOf"/>.</summary>
public static class HttpDownload
{
    /// <summary>Report at most twice a second, or after 512 KiB — whichever
    /// comes first — plus one final report at the end.</summary>
    private const int ReportIntervalMs = 500;
    private const long ReportIntervalBytes = 512 * 1024;

    public static string Message(string label, long received, long total) =>
        total > 0
            ? $"{label}… {Math.Min(100, 100 * received / total)}% ({FormatBytes(received)} / {FormatBytes(total)})"
            : $"{label}… {FormatBytes(received)}";

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>The percentage of a progress message, or null when the
    /// message does not contain one (e.g. git transfers report bytes only).</summary>
    public static double? PercentOf(string message)
    {
        var percent = message.IndexOf('%');
        if (percent <= 0)
            return null;
        var start = percent;
        while (start > 0 && (char.IsDigit(message[start - 1]) || message[start - 1] == '.'))
            start--;
        return double.TryParse(message[start..percent], out var value) ? value : null;
    }

    /// <summary>Download a URL to a file, reporting progress as
    /// "label… N% (X MB / Y MB)" (or just the byte count when the server
    /// sends no usable content length).</summary>
    public static async Task ToFileAsync(
        HttpClient http, string url, string destination,
        IProgress<string>? progress, string label, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = UsableTotal(response);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destination);
        await CopyAsync(source, target, total, progress, label, ct);
    }

    /// <summary>Download a URL into memory with the same progress reporting
    /// (used for the ~22 MB match list).</summary>
    public static async Task<byte[]> ToBytesAsync(
        HttpClient http, string url, IProgress<string>? progress, string label,
        CancellationToken ct = default)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = UsableTotal(response);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var target = new MemoryStream();
        await CopyAsync(source, target, total, progress, label, ct);
        return target.ToArray();
    }

    /// <summary>The content length we can report against. Compressed
    /// responses report the compressed size while the stream yields the
    /// decompressed bytes, so a percentage against it would be wrong — those
    /// downloads show byte counts instead.</summary>
    private static long UsableTotal(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(e =>
                e.Contains("gzip", StringComparison.OrdinalIgnoreCase) ||
                e.Contains("deflate", StringComparison.OrdinalIgnoreCase)))
            return 0;
        return response.Content.Headers.ContentLength ?? 0;
    }

    private static async Task CopyAsync(
        Stream source, Stream target, long total, IProgress<string>? progress,
        string label, CancellationToken ct)
    {
        var buffer = new byte[81920];
        var stopwatch = Stopwatch.StartNew();
        long received = 0;
        long lastReported = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            if (progress is null)
                continue;
            if (received - lastReported < ReportIntervalBytes &&
                stopwatch.ElapsedMilliseconds < ReportIntervalMs)
                continue;
            lastReported = received;
            stopwatch.Restart();
            progress.Report(Message(label, received, total));
        }
        if (progress is not null && lastReported != received)
            progress.Report(Message(label, received, total));
    }
}
