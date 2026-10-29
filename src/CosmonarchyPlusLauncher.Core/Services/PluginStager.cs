using CosmonarchyPlusLauncher.Core.Models;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Stages plugin .qdp files into the build's plugins/ directory and
/// manages rollback arming, mirroring the variant behaviour of the original
/// launcher's script suite (original / plus / netcode).
///
/// <para><b>Plus Netcode is applied only when hosting or joining as a
/// guest.</b> Solo play (and Original CM) runs without the rollback plugin
/// present at all, not merely disarmed — netcode is an online-only
/// component, never shipped inert into offline builds.</para></summary>
public sealed class PluginStager
{
    private readonly string _pluginsDir;
    private readonly string _stageDir;
    private readonly string _home;

    public PluginStager(string buildDir, string stageDir, string home)
    {
        _pluginsDir = Path.Combine(buildDir, "plugins");
        _stageDir = stageDir;
        _home = home;
    }

    /// <summary>Stage the given variant into plugins/. Host/Guest apply the
    /// netcode plugin and arm it; Solo and Original CM leave it out.</summary>
    public void Apply(Variant variant, NetcodeRole? role)
    {
        Directory.CreateDirectory(_pluginsDir);

        switch (variant)
        {
            case Variant.Original:
                UnstagePlus();
                UnstageRollback();
                Disarm();
                break;

            case Variant.Netcode:
                UnstagePlus();
                StageRollback();
                Arm(role ?? NetcodeRole.Host);
                break;

            case Variant.Plus:
                StagePlus();
                // Online is part of Plus, but the netcode plugin applies only
                // when hosting or joining as a guest. Solo never contains it:
                // the armed host/join path drives the netcode's turn system,
                // which ignores ordinary mouse/keyboard input while it is
                // under development.
                if (role is null or NetcodeRole.Solo)
                {
                    UnstageRollback();
                    Disarm();
                }
                else
                {
                    StageRollback();
                    Arm(role.Value);
                }
                break;
        }
    }

    public void UnstagePlus() => File.Delete(Path.Combine(_pluginsDir, "zz-cosmo-plus.qdp"));

    /// <summary>Remove the netcode plugin from the build (Solo and Original
    /// CM never contain it). The existing copy is kept in the shared stage
    /// dir rather than deleted, so the next Host/Guest play can re-apply it
    /// even when this machine has no local build and the checkout is dirty
    /// enough to skip the fetch/reset that would restore it.</summary>
    public void UnstageRollback()
    {
        var path = Path.Combine(_pluginsDir, "zz-cosmo-rollback.qdp");
        if (!File.Exists(path))
            return;
        try
        {
            File.Copy(path, Path.Combine(_stageDir, "zz-cosmo-rollback.qdp"), overwrite: true);
            File.Delete(path);
        }
        catch (IOException)
        {
            // Housekeeping must never block a play; the file simply stays.
        }
    }

    /// <summary>Newest of: a local release/debug build, or a copy staged in the
    /// variants directory. A managed source (local build or variants copy)
    /// always wins over the checkout's bundled plugin — git checkout stamps
    /// files with "now", so a mtime comparison would keep a stale bundled
    /// plugin over a freshly built one and ship old code to the game.</summary>
    public void StagePlus()
    {
        var managed = new[]
        {
            Path.Combine(_home, "Projects", "cosmonarchy-plus", "plus", "target",
                "i686-pc-windows-msvc", "release", "cosmo_plus.dll"),
            Path.Combine(_home, "Projects", "cosmonarchy-plus", "plus", "target",
                "i686-pc-windows-msvc", "debug", "cosmo_plus.dll"),
            Path.Combine(_stageDir, "zz-cosmo-plus.qdp"),
        };
        StageManaged(managed, "Plus features", "zz-cosmo-plus.qdp");
    }

    public void StageRollback()
    {
        var managed = new[]
        {
            Path.Combine(_home, "Projects", "cosmonarchy-rollback", "target",
                "i686-pc-windows-msvc", "release", "cosmo_rollback.dll"),
            Path.Combine(_home, "Projects", "cosmonarchy-rollback", "target",
                "i686-pc-windows-msvc", "debug", "cosmo_rollback.dll"),
            Path.Combine(_stageDir, "zz-cosmo-rollback.qdp"),
        };
        StageManaged(managed, "Plus Netcode", "zz-cosmo-rollback.qdp");
    }

    /// <summary>Copy the first existing managed source to the build. Managed
    /// sources express explicit user/dev intent (a fresh local build, or a
    /// plugin package installed through the launcher), so they always
    /// overwrite the checkout's bundled plugin — regardless of mtimes, which
    /// git resets make meaningless. Only when no managed source exists does
    /// the checkout's bundled plugin stand.</summary>
    private void StageManaged(IEnumerable<string> sources, string name, string targetName)
    {
        var target = Path.Combine(_pluginsDir, targetName);
        foreach (var src in sources)
        {
            if (!File.Exists(src))
                continue;
            File.Copy(src, target, overwrite: true);
            return;
        }

        if (!File.Exists(target))
            throw new InvalidOperationException(
                $"No {name} plugin found: build it, or put a copy in {_stageDir}/{targetName}");
    }

    /// <summary>Arm the rollback plugin for the given slot. On Linux the
    /// arming travels through the process environment; a stale
    /// cosmo_rollback.conf is removed so it cannot arm every launch.</summary>
    public void Arm(NetcodeRole role)
    {
        var slot = role == NetcodeRole.Guest ? "1" : "0";
        Environment.SetEnvironmentVariable("CM_ROLLBACK_LIVE", "1");
        Environment.SetEnvironmentVariable("CM_ROLLBACK_SLOT", slot);
        foreach (var conf in new[]
        {
            Path.Combine(_pluginsDir, "cosmo_rollback.conf"),
            Path.Combine(Path.GetDirectoryName(_pluginsDir)!, "cosmo_rollback.conf"),
        })
            File.Delete(conf);
    }

    public void Disarm()
    {
        Environment.SetEnvironmentVariable("CM_ROLLBACK_LIVE", null);
        Environment.SetEnvironmentVariable("CM_ROLLBACK_SLOT", null);
        foreach (var conf in new[]
        {
            Path.Combine(_pluginsDir, "cosmo_rollback.conf"),
            Path.Combine(Path.GetDirectoryName(_pluginsDir)!, "cosmo_rollback.conf"),
        })
            File.Delete(conf);
    }
}
