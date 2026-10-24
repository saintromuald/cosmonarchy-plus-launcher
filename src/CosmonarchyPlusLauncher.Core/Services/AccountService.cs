using System.Net.Http.Headers;
using System.Text.Json;
using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>cosmonarchy.com account linking. Client side only: the backend
/// belongs to the No-Frauds Club; we implement the documented flow that the
/// original launcher uses (device-style login: request an id, open the auth
/// page, poll for the token).</summary>
public sealed class AccountService
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://cosmonarchy.com/"),
    };

    public async Task<LoginRequest> StartLoginAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("/api/auth", ct);
        var requestId = json.GetProperty("loginRequestId").GetString() ?? "";
        var authUrl = json.GetProperty("authUrl").GetString() ?? "";
        return new LoginRequest(requestId, authUrl);
    }

    /// <summary>The backend sometimes points Discord at its staging domain
    /// (stars.veeq7.com), which the Discord app registration no longer
    /// accepts — Discord rejects it with "Invalid OAuth2 redirect_uri". Both
    /// domains serve the same backend (the callback is identical), so rewrite
    /// the redirect to the production domain. The state (which ties the
    /// browser callback to the launcher's poll) is left untouched.</summary>
    internal static string FixAuthUrl(string authUrl)
    {
        try
        {
            var builder = new UriBuilder(authUrl);
            var isDiscord = builder.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
                            builder.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase);
            if (!isDiscord)
                return authUrl;

            var parts = builder.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            for (var i = 0; i < parts.Count; i++)
            {
                var pair = parts[i].Split('=', 2);
                if (pair.Length != 2 || !pair[0].Equals("redirect_uri", StringComparison.OrdinalIgnoreCase))
                    continue;
                var redirect = new Uri(Uri.UnescapeDataString(pair[1]));
                var host = redirect.Host;
                if (host.Equals("cosmonarchy.com", StringComparison.OrdinalIgnoreCase) ||
                    host.Equals("www.cosmonarchy.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                pair[1] = Uri.EscapeDataString(
                    new UriBuilder(redirect) { Host = "cosmonarchy.com", Port = -1 }.ToString());
                parts[i] = string.Join('=', pair);
            }

            builder.Port = -1; // don't render the default ports UriBuilder infers
            builder.Query = string.Join('&', parts);
            return builder.ToString();
        }
        catch (UriFormatException)
        {
            return authUrl; // not our URL to fix: open it as-is
        }
    }

    public async Task<string?> PollLoginAsync(string requestId, TimeSpan pollInterval, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(pollInterval, ct);
            var json = await GetJsonAsync($"/api/auth/status?requestId={Uri.EscapeDataString(requestId)}", ct);
            if (json.GetProperty("completed").GetBoolean())
                return json.GetProperty("token").GetString();
        }
        return null;
    }

    public async Task<User> LoadUserAsync(string token, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/users/@me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        return new User(
            root.GetProperty("discordId").GetString() ?? "",
            root.GetProperty("username").GetString() ?? "",
            root.GetProperty("email").GetString() ?? "",
            root.GetProperty("matchesPlayed").GetInt32(),
            root.GetProperty("matchesWon").GetInt32(),
            root.GetProperty("mmr").GetInt32());
    }

    private static async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var res = await Http.GetAsync(path, ct);
        res.EnsureSuccessStatusCode();
        var json = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.Clone();
    }
}

public sealed record LoginRequest(string RequestId, string AuthUrl);
