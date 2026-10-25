namespace CosmonarchyPlusLauncher.Core.Models;

/// <summary>A locally stored statistics account: email + password (PBKDF2,
/// never plaintext), the in-game name the account claims, and the matches the
/// account has certified as "really me". When the self-hosted backend lands
/// (see ROADMAP.md) the same shape travels as JSON over the account API.</summary>
public sealed class LocalAccount
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string PlayerName { get; set; } = "";

    /// <summary>Base64, 16 bytes, random per account.</summary>
    public string Salt { get; set; } = "";

    /// <summary>Base64 PBKDF2-SHA256 of the password (210k iterations).</summary>
    public string PasswordHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>ReplayKraken match UUIDs this account has certified as
    /// genuinely played by <see cref="PlayerName"/>.</summary>
    public List<string> CertifiedMatchUuids { get; set; } = [];

    public string CreatedLabel => CreatedAt.LocalDateTime.ToString("yyyy-MM-dd");
}
