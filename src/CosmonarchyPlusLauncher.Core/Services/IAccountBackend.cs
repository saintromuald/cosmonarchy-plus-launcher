using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>A signed-in account plus the credential the backend issued.
/// The local backend leaves <see cref="Token"/> empty; the future server
/// backend returns the bearer token its API expects.</summary>
public sealed record AccountSession(LocalAccount Account, string Token);

/// <summary>The seam between the statistics tab and wherever accounts live.
/// Today only <see cref="LocalAccountBackend"/> exists (accounts and
/// certifications stored on this machine). ROADMAP.md specifies the HTTP
/// contract a self-hosted implementation must satisfy so it can drop in
/// without touching the UI.</summary>
public interface IAccountBackend
{
    /// <summary>Authenticate. Returns null when the credentials are wrong.</summary>
    Task<AccountSession?> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Create an account. <see cref="AccountSession"/> is null when
    /// registration is refused; <paramref name="error"/> explains why.</summary>
    Task<(AccountSession? Session, string Error)> RegisterAsync(
        string email, string password, string playerName, CancellationToken ct = default);

    /// <summary>Change the in-game name the account's statistics track.</summary>
    Task UpdatePlayerNameAsync(AccountSession session, string playerName, CancellationToken ct = default);

    /// <summary>Certify (or un-certify) that one ReplayKraken match was
    /// genuinely played by the account's claimed name.</summary>
    Task SetCertificationAsync(
        AccountSession session, string matchUuid, bool certified, CancellationToken ct = default);
}
