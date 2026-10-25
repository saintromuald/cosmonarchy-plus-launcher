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
        var account = session.Account;
        account.Certifications.RemoveAll(c =>
            c.MatchUuid.Equals(matchUuid, StringComparison.OrdinalIgnoreCase));
        if (certified)
        {
            account.Certifications.Add(new Certification
            {
                MatchUuid = matchUuid,
                CertifiedAt = DateTimeOffset.Now,
                Email = account.Email,
                PlayerName = account.PlayerName,
            });
        }
        Save();
        return Task.CompletedTask;
    }

    /// <summary>Case-insensitive set of the account's certified match UUIDs.</summary>
    public static HashSet<string> CertifiedSet(LocalAccount account) =>
        new(account.Certifications.Select(c => c.MatchUuid), StringComparer.OrdinalIgnoreCase);

    /// <summary>The certification record for one match, or null when the
    /// account has not certified it. Carries who certified (email + name).</summary>
    public static Certification? CertificationFor(LocalAccount account, string matchUuid) =>
        account.Certifications.FirstOrDefault(c =>
            c.MatchUuid.Equals(matchUuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every account stored on this machine, sorted by email. These
    /// are the accounts the switcher offers (all previously authenticated —
    /// their passwords were verified when they were added).</summary>
    public IReadOnlyList<LocalAccount> ListAccounts()
    {
        lock (_gate)
        {
            return LoadLocked()
                .OrderBy(a => a.Email, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>The stored account with this id, or null. Used to restore the
    /// last signed-in account at startup.</summary>
    public LocalAccount? FindAccountById(string id)
    {
        lock (_gate)
        {
            return LoadLocked().FirstOrDefault(a =>
                a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

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
                var migrated = false;
                foreach (var account in accounts)
                {
                    // Pre-identity account files stored bare UUID strings.
                    if (account.Certifications.Count == 0 && account.CertifiedMatchUuids.Count > 0)
                    {
                        account.Certifications = account.CertifiedMatchUuids
                            .Select(u => new Certification
                            {
                                MatchUuid = u,
                                CertifiedAt = account.CreatedAt,
                                Email = account.Email,
                                PlayerName = account.PlayerName,
                            })
                            .ToList();
                        account.CertifiedMatchUuids = [];
                        migrated = true;
                    }
                }
                _accounts = accounts;
                if (migrated)
                    SaveLocked(accounts);
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
