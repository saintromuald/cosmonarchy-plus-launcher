using Avalonia.Controls;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.Views;

public partial class MainWindow : Window
{
    /// <summary>True when running under the Hyprland compositor. Hyprland
    /// ignores client maximize requests, so a window rule in the Hyprland
    /// config owns the launcher's fullscreen state instead (see the
    /// `CosmonarchyPlusLauncher` window rule); the app must not fight it
    /// with repeated state requests, which lose the race and leave the
    /// window tiled at quarter size.</summary>
    private static bool IsHyprland =>
        Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.Contains("Hyprland", StringComparison.OrdinalIgnoreCase) == true
        || Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") is { Length: > 0 };

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            AppRuntime.Clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            // Non-Hyprland desktops get a plain maximize; Hyprland's window
            // rule applies real fullscreen without any client requests.
            if (!IsHyprland)
                WindowState = WindowState.Maximized;
        };
    }
}
