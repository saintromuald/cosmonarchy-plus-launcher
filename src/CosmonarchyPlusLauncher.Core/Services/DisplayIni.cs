using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>cnc-ddraw display configuration for Play launches.
///
/// The game always opens fullscreen from the Play button: windowed is cleared,
/// fullscreen set, and width/height are 0, which cnc-ddraw resolves to the
/// desktop's native resolution — so the game fills the screen on any monitor.
/// The windowed/fullscreen toggle is moved off Alt+Enter (which Cosmonarchy
/// uses for other bindings) to Alt+F11 (VK 0x7A). All other keys (vsync,
/// maxfps, shaders, renderer) are preserved.</summary>
internal static class DisplayIni
{
    /// <summary>VK code for the key that toggles windowed/fullscreen when
    /// pressed together with Alt. 0x7A = F11 (F9/F12 are Plus hotkeys,
    /// F10 is the game menu; Ctrl+F11 in the rollback plugin uses a different
    /// modifier, so Alt+F11 is free).</summary>
    internal const string ToggleKey = "0x7A";

    internal const string Fullscreen =
        "[ddraw]\nkeytogglefullscreen=0x7A\nwidth=0\nheight=0\nfullscreen=true\nwindowed=false\nrenderer=auto\n";

    /// <summary>Rewrite the display keys inside every [ddraw] section of an
    /// existing ini; append a [ddraw] section if the file has none. The toggle
    /// key is added when absent (otherwise cnc-ddraw falls back to its default
    /// Alt+Enter). Keys outside sections are left untouched.</summary>
    internal static string Patch(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var inDdraw = false;
        var seenDdraw = false;
        var haveToggle = false;
        var firstDdrawIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inDdraw = trimmed.Equals("[ddraw]", System.StringComparison.OrdinalIgnoreCase);
                if (inDdraw)
                {
                    seenDdraw = true;
                    if (firstDdrawIndex < 0)
                        firstDdrawIndex = i;
                }
                continue;
            }
            if (!inDdraw)
                continue;
            var key = trimmed.Split('=')[0].Trim().ToLowerInvariant();
            switch (key)
            {
                case "windowed": lines[i] = "windowed=false"; break;
                case "fullscreen": lines[i] = "fullscreen=true"; break;
                case "width": lines[i] = "width=0"; break;
                case "height": lines[i] = "height=0"; break;
                case "keytogglefullscreen":
                    lines[i] = $"keytogglefullscreen={ToggleKey}";
                    haveToggle = true;
                    break;
            }
        }
        if (seenDdraw && !haveToggle && firstDdrawIndex >= 0)
            lines.Insert(firstDdrawIndex + 1, $"keytogglefullscreen={ToggleKey}");
        var result = string.Join("\n", lines);
        if (!seenDdraw)
            result = result.TrimEnd('\n') +
                "\n\n[ddraw]\nkeytogglefullscreen=0x7A\nwidth=0\nheight=0\nfullscreen=true\nwindowed=false\n";
        return result;
    }
}
