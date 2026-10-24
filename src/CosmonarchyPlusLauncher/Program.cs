using Avalonia;
using System;

namespace CosmonarchyPlusLauncher;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    //
    // Prefer the native Wayland backend when a compositor is available so the
    // window gets per-monitor scaling from the compositor (e.g. 2x on a
    // HiDPI handheld panel, 1x on an external 1080p screen). On X11/XWayland
    // Avalonia renders at one fixed scale, which makes the UI come out tiny
    // on scaled displays; UseWaylandWithFallback keeps X11 as the fallback.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWaylandWithFallback()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
