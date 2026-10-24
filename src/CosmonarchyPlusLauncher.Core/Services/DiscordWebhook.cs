using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Posts share messages to a Discord webhook (channel → Integrations
/// → Webhooks). Optional: sharing works without one via the clipboard.</summary>
public sealed class DiscordWebhook
{
    private readonly HttpClient _http;

    public DiscordWebhook(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public static bool IsValidWebhookUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)) &&
        uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);

    public async Task PostAsync(string webhookUrl, string content, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { content });
        using var response = await _http.PostAsync(
            webhookUrl, new StringContent(json, Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Posts a message with the share payload attached as a .json
    /// file — the way larger lists travel through Discord.</summary>
    public async Task PostWithFileAsync(
        string webhookUrl, string content, string fileName, string fileContent, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(
            new StringContent(JsonSerializer.Serialize(new { content }), Encoding.UTF8, "application/json"),
            "payload_json");
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(fileContent));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(file, "file", fileName);

        using var response = await _http.PostAsync(webhookUrl, form, ct);
        response.EnsureSuccessStatusCode();
    }
}
