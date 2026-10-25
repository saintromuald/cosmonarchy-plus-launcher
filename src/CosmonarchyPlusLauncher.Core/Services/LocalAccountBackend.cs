using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Email + password accounts stored in a JSON file under the
/// launcher's data directory. Passwords are salted PBKDF2-SHA256 (210k
/// iterations) and compared in constant time. This is the local
/// <see cref="IAccountBackend"/>; a self-hosted server implementation is
/// specified in ROADMAP.md.</summary>
public sealed class LocalAccountBackend : IAccountBackend
{
    public const int MinPasswordLength = 8;

    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 210_000;

    private readonly string _filePath;
    private readonly object _gate = new();
    private List<LocalAccount>? _accounts;

    public LocalAccountBackend(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cosmonarchy-plus-launcher", "local-accounts.json");
    }

    public string FilePath => _filePath;

    public Task<AccountSession?> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var account = Find(email);
        if (account is null || !Verify(password, account))
            return Task.FromResult<AccountSession?>(null);
        return Task.FromResult<AccountSession?>(new AccountSession(account, ""));
    }

    public Task<(AccountSession? Session, string Error)> RegisterAsync(
        string email, string password, string playerName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = email.Trim();
        if (!IsPlausibleEmail(normalized))
            return Task.FromResult<(AccountSession?, string)>((null, "Enter a valid email address."));
        if (password.Length < MinPasswordLength)
            return Task.FromResult<(AccountSession?, string)>((null, $"Password must be at least {MinPasswordLength} characters."));
        if (string.IsNullOrWhiteSpace(playerName))
            return Task.FromResult<(AccountSession?, string)>((null, "Enter the in-game name this account tracks."));
        if (Find(normalized) is not null)
            return Task.FromResult<(AccountSession?, string)>((null, "An account with that email already exists."));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var account = new LocalAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = normalized,
            PlayerName = playerName.Trim(),
            Salt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(Hash(password, salt)),
            CreatedAt = DateTimeOffset.Now,
        };
        lock (_gate)
        {
            var accounts = LoadLocked();
            accounts.Add(account);
            SaveLocked(accounts);
        }
        return Task.FromResult<(AccountSession?, string)>((new AccountSession(account, ""), ""));
    }

    public Task UpdatePlayerNameAsync(AccountSession session, string playerName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(playerName))
            return Task.CompletedTask;
        session.Account.PlayerName = playerName.Trim();
        Save();
        return Task.CompletedTask;
    }

    public Task SetCertificationAsync(AccountSession session, string matchUuid, bool certified, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var uuids = session.Account.CertifiedMatchUuids;
        uuids.RemoveAll(u => u.Equals(matchUuid, StringComparison.OrdinalIgnoreCase));
        if (certified)
            uuids.Add(matchUuid);
        Save();
        return Task.CompletedTask;
    }

    /// <summary>Case-insensitive set of the account's certified match UUIDs.</summary>
    public static HashSet<string> CertifiedSet(LocalAccount account) =>
        new(account.CertifiedMatchUuids, StringComparer.OrdinalIgnoreCase);

    // ---- internals ---------------------------------------------------------

    private static bool IsPlausibleEmail(string email) =>
        email.Length >= 3 && email.Contains('@') && email.Contains('.') && !email.Contains(' ');

    private LocalAccount? Find(string email)
    {
        lock (_gate)
        {
            return LoadLocked().FirstOrDefault(a =>
                a.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool Verify(string password, LocalAccount account)
    {
        try
        {
            var salt = Convert.FromBase64String(account.Salt);
            var expected = Convert.FromBase64String(account.PasswordHash);
            var actual = Hash(password, salt);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);

    private List<LocalAccount> LoadLocked()
    {
        if (_accounts is not null)
            return _accounts;
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var accounts = JsonSerializer.Deserialize<List<LocalAccount>>(json) ?? [];
                _accounts = accounts;
                return _accounts;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt account file must never brick the tab; start fresh.
        }
        _accounts = [];
        return _accounts;
    }

    private void Save()
    {
        lock (_gate)
        {
            SaveLocked(LoadLocked());
        }
    }

    private void SaveLocked(List<LocalAccount> accounts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(accounts, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _filePath, overwrite: true);
        _accounts = accounts;
    }
}
