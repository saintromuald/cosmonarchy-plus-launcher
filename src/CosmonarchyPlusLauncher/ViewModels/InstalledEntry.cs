using Avalonia.Media;
using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.ViewModels;

/// <summary>One row in the Play page's installed list. Game versions and
/// plugin builds share the list (and the variant filter) but expose
/// different actions: only a game version can be played.</summary>
public sealed class InstalledEntry
{
    private static readonly SolidColorBrush OriginalBrush = new(Color.Parse("#64748B"));
    private static readonly SolidColorBrush PlusBrush = new(Color.Parse("#2563EB"));
    private static readonly SolidColorBrush NetcodeBrush = new(Color.Parse("#C2410C"));

    public InstalledEntry(GameVersion version)
    {
        Version = version;
        Name = version.Name;
        Summary = version.Summary;
        Variant = version.EffectiveVariant;
        KindLabel = "Build";
    }

    public InstalledEntry(PluginVersion plugin)
    {
        Plugin = plugin;
        Name = plugin.Name;
        Summary = plugin.Summary;
        Variant = plugin.Variant;
        KindLabel = "Plugin";
    }

    public GameVersion? Version { get; }
    public PluginVersion? Plugin { get; }
    public Variant Variant { get; }
    public string Name { get; }
    public string Summary { get; }
    public string KindLabel { get; }

    public string Id => Version is not null ? $"v:{Version.Id}" : $"p:{Plugin!.Id}";

    public string Badge => Variant.BadgeText();

    public IBrush BadgeBrush => Variant switch
    {
        Variant.Plus => PlusBrush,
        Variant.Netcode => NetcodeBrush,
        _ => OriginalBrush,
    };

    public bool IsPlugin => Plugin is not null;
    public bool CanPlay => Version is not null;

    public string PlayHint => CanPlay
        ? "Play this build with the variant's plugin set."
        : "Not a game version — this plugin is applied automatically when you play a build. Select a build to play.";
}
