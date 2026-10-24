using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Octokit;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>One public share on the community board.</summary>
public sealed class CommunityShare
{
    /// <summary>File name inside the shares/ directory (e.g. "cosmonarchy-3f9a2c.json").</summary>
    public string FileName { get; set; } = "";
    /// <summary>The payload format (<see cref="ReplaySharing.ListFormat"/> etc.).</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>The sharer's site/Discord name; empty when they were not logged in.</summary>
    public string DiscordName { get; set; } = "";
    public string DiscordId { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
    public int GameCount { get; set; }
    public int NoteCount { get; set; }

    public string AuthorLabel => DiscordName.Length > 0 ? DiscordName : "anonymous";

    public string KindLabel => Kind switch
    {
        ReplaySharing.ListFormat => "List",
        ReplaySharing.FavoritesFormat => "Favourites",
        ReplaySharing.NoteFormat => "Note",
        _ => "Share",
    };

    public string SavedAtLabel => SavedAt == DateTimeOffset.MinValue
        ? ""
        : SavedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string SummaryLabel => GameCount > 0 || NoteCount > 0
        ? $"{GameCount} game(s) · {NoteCount} note(s)"
        : "";
}

/// <summary>The community share board: a public GitHub repo. The feed is one
/// file (<see cref="IndexPath"/>) listing every share; each share is a JSON
/// payload under <see cref="SharesDirectory"/>. Reading needs no
/// authentication; publishing (and unpublishing your own shares) needs a
/// token from Settings with contents write on the repo.</summary>
public sealed class CommunityService
{
    public const string IndexFormat = "cosmonarchy-plus-launcher/community-index";
    public const string IndexPath = "index.json";
    public const string SharesDirectory = "shares";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public CommunityService(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    // ---- Feed --------------------------------------------------------------

    public async Task<IReadOnlyList<CommunityShare>> ListSharesAsync(string repo, CancellationToken ct = default)
    {
        var client = NewClient(token: null);
        try
        {
            var index = await GetIndexContentsAsync(client, repo, ct);
            if (index is null)
                return [];
            return ParseIndex(Decode(index.Content));
        }
        catch (NotFoundException)
        {
            throw new InvalidOperationException(
                $"The community repo “{repo}” does not exist or has no {IndexPath} yet. Create it as a public GitHub repo — publishing the first share creates the index automatically.");
        }
        catch (RateLimitExceededException)
        {
            throw new InvalidOperationException(
                "GitHub's anonymous rate limit is reached (60 requests/hour). Wait a little, or add a GitHub token in Settings to get a much higher limit.");
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                $"GitHub refused anonymous access to “{repo}” — the repo must be public to appear in the community feed.");
        }
    }

    public async Task<string> FetchShareAsync(string repo, string fileName, CancellationToken ct = default)
    {
        var client = NewClient(token: null);
        var path = $"{SharesDirectory}/{fileName}";
        try
        {
            var contents = await client.Repository.Content.GetAllContents(
                Owner(repo), Name(repo), path);
            var file = contents.FirstOrDefault();
            if (file is null)
                throw new InvalidOperationException($"Share “{fileName}” is not in “{repo}”.");
            if (!string.IsNullOrWhiteSpace(file.Content))
                return Decode(file.Content);
            return await _http.GetStringAsync(file.DownloadUrl, ct);
        }
        catch (NotFoundException)
        {
            throw new InvalidOperationException($"Share “{fileName}” is not in “{repo}” (it may have been unpublished).");
        }
    }

    // ---- Publishing ---------------------------------------------------------

    public async Task PublishAsync(
        string repo, string token, CommunityShare share, string payloadJson, CancellationToken ct = default)
    {
        var client = NewClient(token);
        var (owner, name) = Split(repo);
        var path = $"{SharesDirectory}/{share.FileName}";
        try
        {
            await client.Repository.Content.CreateFile(
                owner, name, path,
                new CreateFileRequest($"Share “{share.Name}”", Encode(payloadJson)));
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
        catch (ApiValidationException)
        {
            throw new InvalidOperationException(
                $"“{share.FileName}” already exists in the repo — press the button again (names are random, so this is very unlikely).");
        }
        await UpsertIndexAsync(client, owner, name, share, remove: false, ct);
    }

    public async Task UnpublishAsync(string repo, string token, CommunityShare share, CancellationToken ct = default)
    {
        var client = NewClient(token);
        var (owner, name) = Split(repo);
        var path = $"{SharesDirectory}/{share.FileName}";
        try
        {
            var contents = await client.Repository.Content.GetAllContents(owner, name, path);
            var file = contents.FirstOrDefault();
            if (file is not null)
            {
                await client.Repository.Content.DeleteFile(
                    owner, name, path, new DeleteFileRequest($"Unpublish “{share.Name}”", file.Sha));
            }
        }
        catch (NotFoundException)
        {
            // Already gone; just drop it from the index.
        }
        catch (AuthorizationException)
        {
            throw new InvalidOperationException(
                "GitHub rejected the token — only the author (with a valid token) can unpublish a share.");
        }
        await UpsertIndexAsync(client, owner, name, share, remove: true, ct);
    }

    private async Task UpsertIndexAsync(
        GitHubClient client, string owner, string name, CommunityShare share, bool remove, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            RepositoryContent? index = null;
            try
            {
                index = await GetIndexContentsAsync(client, $"{owner}/{name}", ct);
            }
            catch (NotFoundException)
            {
                // No index yet — the CreateFile below starts one.
            }

            var shares = index is null
                ? new List<CommunityShare>()
                : ParseIndex(Decode(index.Content)).ToList();
            shares.RemoveAll(s => s.FileName.Equals(share.FileName, StringComparison.OrdinalIgnoreCase));
            if (!remove)
                shares.Insert(0, share);
            var json = SerializeIndex(shares);

            try
            {
                if (index is null)
                {
                    await client.Repository.Content.CreateFile(
                        owner, name, IndexPath, new CreateFileRequest("Create share index", Encode(json)));
                }
                else
                {
                    await client.Repository.Content.UpdateFile(
                        owner, name, IndexPath, new UpdateFileRequest("Update share index", Encode(json), index.Sha));
                }
                return;
            }
            catch (ApiValidationException) when (attempt == 0)
            {
                // Someone else updated the index between our read and write;
                // re-read once and retry.
            }
        }
        throw new InvalidOperationException(
            "Could not update the community index — several people published at the same moment. Try again.");
    }

    private async Task<RepositoryContent?> GetIndexContentsAsync(GitHubClient client, string repo, CancellationToken ct)
    {
        try
        {
            var contents = await client.Repository.Content.GetAllContents(Owner(repo), Name(repo), IndexPath);
            return contents.FirstOrDefault();
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    // ---- Serialisation ------------------------------------------------------

    internal static IReadOnlyList<CommunityShare> ParseIndex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var shares = new List<CommunityShare>();
        if (!TryProp(root, "shares", out var array) || array.ValueKind != JsonValueKind.Array)
            return shares;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var file = Get(item, "file");
            if (file.Length == 0)
                continue;
            var share = new CommunityShare
            {
                FileName = file,
                Kind = Get(item, "kind"),
                Name = Get(item, "name"),
                Title = Get(item, "title"),
                DiscordName = GetAuthor(item, "discordName"),
                DiscordId = GetAuthor(item, "discordId"),
                SavedAt = ParseDate(Get(item, "savedAt")) ?? DateTimeOffset.MinValue,
                GameCount = GetInt(item, "gameCount"),
                NoteCount = GetInt(item, "noteCount"),
            };
            if (share.Name.Length == 0)
                share.Name = share.FileName;
            shares.Add(share);
        }
        return shares.OrderByDescending(s => s.SavedAt).ToList();
    }

    internal static string SerializeIndex(IEnumerable<CommunityShare> shares) =>
        JsonSerializer.Serialize(new
        {
            format = IndexFormat,
            version = 1,
            shares = shares.Select(s => (object)new
            {
                file = s.FileName,
                kind = s.Kind,
                name = s.Name,
                title = s.Title,
                author = s.DiscordName.Length > 0 || s.DiscordId.Length > 0
                    ? new { discordName = s.DiscordName, discordId = s.DiscordId }
                    : null,
                savedAt = s.SavedAt.ToString("O"),
                gameCount = s.GameCount,
                noteCount = s.NoteCount,
            }).ToList(),
        }, JsonOptions);

    // ---- Helpers ------------------------------------------------------------

    private static GitHubClient NewClient(string? token)
    {
        var client = new GitHubClient(new ProductHeaderValue("CosmonarchyPlusLauncher"));
        if (!string.IsNullOrEmpty(token))
            client.Credentials = new Credentials(token);
        return client;
    }

    private static (string Owner, string Name) Split(string repo)
    {
        var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new InvalidOperationException(
                $"“{repo}” is not a GitHub repo — use the form owner/name (e.g. saintromuald/cosmonarchy-shares).");
        return (parts[0], parts[1]);
    }

    private static string Owner(string repo) => Split(repo).Owner;

    private static string Name(string repo) => Split(repo).Name;

    private static string Encode(string content) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

    private static string Decode(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("The share came back empty from GitHub.");
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "")));
        }
        catch (FormatException)
        {
            return content; // In case GitHub already handed back plain text.
        }
    }

    private static bool TryProp(JsonElement element, string name, out JsonElement value) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);

    private static string Get(JsonElement element, string name) =>
        TryProp(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string GetAuthor(JsonElement element, string name) =>
        TryProp(element, "author", out var author) ? Get(author, name) : "";

    private static int GetInt(JsonElement element, string name) =>
        TryProp(element, name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static DateTimeOffset? ParseDate(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : null;
}
