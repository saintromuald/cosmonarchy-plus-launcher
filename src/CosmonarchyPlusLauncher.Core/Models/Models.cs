namespace CosmonarchyPlusLauncher.Core.Models;

/// <summary>How the game is launched. Mirrors the variant system of the
/// original launcher's plugin staging: a variant is the set of plugin files
/// staged into the build's plugins/ directory plus rollback arming.</summary>
public enum Variant
{
    /// <summary>Vanilla game; no Plus plugin, no rollback arming.</summary>
    Original,

    /// <summary>Plus plugin staged; rollback armed only when a role is chosen.</summary>
    Plus,

    /// <summary>Rollback netcode plugin armed, no Plus content.</summary>
    Netcode,
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

public sealed record Patch(string Id, string Url, string Filename);

public sealed record User(
    string DiscordId,
    string Name,
    string Email,
    int MatchesPlayed,
    int MatchesWon,
    int Mmr
);
