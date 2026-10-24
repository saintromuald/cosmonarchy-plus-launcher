using System.Diagnostics;

namespace CosmonarchyPlusLauncher.Services;

/// <summary>Launching outside apps for the sharing flow.</summary>
public static class DesktopActions
{
    /// <summary>Focuses the Discord client (via its protocol handler) and
    /// falls back to the browser app. The user pastes the clipboard message
    /// into any channel.</summary>
    public static void OpenDiscord()
    {
        try
        {
            Process.Start(new ProcessStartInfo("discord://") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://discord.com/app") { UseShellExecute = true });
            }
            catch
            {
                // No Discord anywhere: the clipboard already holds the message.
            }
        }
    }
}
