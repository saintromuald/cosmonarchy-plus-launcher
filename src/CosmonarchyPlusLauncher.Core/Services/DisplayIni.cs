using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>cnc-ddraw display configuration for Play launches.
///
/// The game always opens fullscreen from the Play button: windowed is cleared,
/// fullscreen set, and width/height are 0, which cnc-ddraw resolves to the
/// desktop's native resolution — so the game fills the screen on any monitor.
/// All other keys (vsync, maxfps, shaders, renderer) are preserved.</summary>
internal static class DisplayIni
{
    internal const string Fullscreen =
        "[ddraw]\nwidth=0\nheight=0\nfullscreen=true\nwindowed=false\nrenderer=auto\n";

    /// <summary>Rewrite the display keys inside every [ddraw] section of an
    /// existing ini; append a [ddraw] section if the file has none. Keys
    /// outside sections are left untouched.</summary>
    internal static string Patch(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var inDdraw = false;
        var seenDdraw = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inDdraw = trimmed.Equals("[ddraw]", System.StringComparison.OrdinalIgnoreCase);
                if (inDdraw)
                    seenDdraw = true;
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
            }
        }
        var result = string.Join("\n", lines);
        if (!seenDdraw)
            result = result.TrimEnd('\n') + "\n\n[ddraw]\nwidth=0\nheight=0\nfullscreen=true\nwindowed=false\n";
        return result;
    }
}
