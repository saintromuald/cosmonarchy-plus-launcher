namespace CosmonarchyPlusLauncher.Core.Models;

/// <summary>How the game is launched. Mirrors the variant system of the
/// original launcher's plugin staging: a variant is the set of plugin files
/// staged into the build's plugins/ directory plus rollback arming.</summary>
public enum Variant
{
    /// <summary>Original CM — the vanilla game; no plugins, no netcode.</summary>
    Original,

    /// <summary>Plus — the full product: Plus gameplay with online
    /// capability. Plus Netcode is applied only when hosting or joining
    /// as guest; Solo plays offline without it.</summary>
    Plus,

    /// <summary>Original CM Netcode — vanilla builds with the Plus Netcode
    /// plugin armed (host or guest), no Plus gameplay content.</summary>
    Netcode,
}

/// <summary>User-facing names for the variant families. The enum member
/// names stay stable (settings store the enum), while these labels carry
/// the agreed product naming: Original CM / Plus / Original CM Netcode.</summary>
public static class VariantDisplay
{
    /// <summary>Full family name ("Original CM", "Plus", "Original CM Netcode").</summary>
    public static string Label(this Variant variant) => variant switch
    {
        Variant.Original => "Original CM",
        Variant.Plus => "Plus",
        Variant.Netcode => "Original CM Netcode",
        _ => variant.ToString(),
    };

    /// <summary>Short badge text ("ORIGINAL CM", "PLUS", "NETCODE").</summary>
    public static string BadgeText(this Variant variant) => variant switch
    {
        Variant.Original => "ORIGINAL CM",
        Variant.Plus => "PLUS",
        Variant.Netcode => "NETCODE",
        _ => variant.ToString().ToUpperInvariant(),
    };
}

/// <summary>Rollback role: host (slot 0), guest (slot 1), or solo
/// (Plus without the netcode turn system).</summary>
public enum NetcodeRole
{
    Host,
    Guest,
    Solo,
}

public sealed record CommitInfo(string Sha, string Message, string Date);

public sealed class Patch
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Filename { get; set; } = "";
}

public sealed record User(
    string DiscordId,
    string Name,
    string Email,
    int MatchesPlayed,
    int MatchesWon,
    int Mmr
);
