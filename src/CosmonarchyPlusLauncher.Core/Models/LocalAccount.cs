namespace CosmonarchyPlusLauncher.Core.Models;

/// <summary>One match certified as "really me": the match, when, and — the
/// part that makes the claim checkable — the certifying account's email and
/// in-game name. Anyone can copy a name; the email is what people publish and
/// recognize. When the self-hosted backend lands (ROADMAP.md) these records
/// travel to the server so every launcher can show who certified what.</summary>
public sealed class Certification
{
    public string MatchUuid { get; set; } = "";
    public DateTimeOffset CertifiedAt { get; set; }

    /// <summary>The certifying account's email (the spoof-resistant identity).</summary>
    public string Email { get; set; } = "";

    /// <summary>The certifying account's claimed in-game name.</summary>
    public string PlayerName { get; set; } = "";

    public string IdentityLabel => $"{PlayerName} ({Email})";
}

/// <summary>A locally stored statistics account: email + password (PBKDF2,
/// never plaintext), the in-game name the account claims, and the matches the
/// account has certified as "really me" — each carrying the certifier's
/// identity. When the self-hosted backend lands (see ROADMAP.md) the same
/// shape travels as JSON over the account API.</summary>
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

    /// <summary>Matches this account certified as genuinely played by
    /// <see cref="PlayerName"/>, with the certifier identity attached.</summary>
    public List<Certification> Certifications { get; set; } = [];

    /// <summary>Legacy field (pre-identity certifications were bare UUID
    /// strings). Only populated when migrating an older account file; cleared
    /// on the next save.</summary>
    public List<string> CertifiedMatchUuids { get; set; } = [];

    public string CreatedLabel => CreatedAt.LocalDateTime.ToString("yyyy-MM-dd");
}
