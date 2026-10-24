using Avalonia.Controls;
using Avalonia.Threading;
using CosmonarchyPlusLauncher.Services;

namespace CosmonarchyPlusLauncher.Views;

public partial class MainWindow : Window
{
    /// <summary>True when running under the Hyprland compositor, which
    /// ignores client maximize requests and only honors real fullscreen.</summary>
    private static bool IsHyprland =>
        Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.Contains("Hyprland", StringComparison.OrdinalIgnoreCase) == true
        || Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") is { Length: > 0 };

    public MainWindow()
    {
        InitializeComponent();
        // Ask the compositor to fill the screen. Hyprland ignores maximize
        // (Omarchy suppresses the maximize event) and drops fullscreen
        // requests made while the window is still being mapped (the Wayland
        // backend has no surface proxy yet at Opened time). Re-issue the
        // request a few times after open; the requests are idempotent, and
        // once one lands the compositor confirms it. WindowState cannot be
        // read back for this because Avalonia echoes the locally-set value.
        Opened += (_, _) =>
        {
            AppRuntime.Clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            ApplyStartupWindowState(attempts: 8);
        };

        void ApplyStartupWindowState(int attempts)
        {
            if (attempts <= 0)
                return;
            WindowState = IsHyprland ? WindowState.FullScreen : WindowState.Maximized;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ApplyStartupWindowState(attempts - 1);
            };
            timer.Start();
        }
    }
}