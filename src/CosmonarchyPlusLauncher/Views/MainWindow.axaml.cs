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
        AppRuntime.MainWindow = this;
        // Fill the screen on launch. Hyprland ignores client maximize
        // requests (Omarchy suppresses the maximize event), so it gets a
        // real fullscreen request; other desktops get maximized. The
        // Wayland backend has no surface proxy yet at Opened time and
        // compositors drop state changes made while the window is still
        // being mapped, so the request is re-issued a few times after open
        // until it lands.
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