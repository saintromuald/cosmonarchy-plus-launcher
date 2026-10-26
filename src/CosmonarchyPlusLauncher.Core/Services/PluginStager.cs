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

    public void UnstageRollback() => File.Delete(Path.Combine(_pluginsDir, "zz-cosmo-rollback.qdp"));

    /// <summary>Newest of: a local release/debug build, or a copy staged in the
    /// variants directory. Picking by mtime avoids launching an older build.</summary>
    public void StagePlus()
    {
        var sources = new[]
        {
            Path.Combine(_home, "Projects", "cosmonarchy-plus", "plus", "target",
                "i686-pc-windows-msvc", "release", "cosmo_plus.dll"),
            Path.Combine(_home, "Projects", "cosmonarchy-plus", "plus", "target",
                "i686-pc-windows-msvc", "debug", "cosmo_plus.dll"),
            Path.Combine(_stageDir, "zz-cosmo-plus.qdp"),
        };
        StageFrom("Plus features", sources, "zz-cosmo-plus.qdp");
    }

    public void StageRollback()
    {
        var sources = new[]
        {
            Path.Combine(_home, "Projects", "cosmonarchy-rollback", "target",
                "i686-pc-windows-msvc", "release", "cosmo_rollback.dll"),
            Path.Combine(_home, "Projects", "cosmonarchy-rollback", "target",
                "i686-pc-windows-msvc", "debug", "cosmo_rollback.dll"),
            Path.Combine(_stageDir, "zz-cosmo-rollback.qdp"),
        };
        StageFrom("Plus Netcode", sources, "zz-cosmo-rollback.qdp");
    }

    private void StageFrom(string name, IEnumerable<string> sources, string targetName)
    {
        var target = Path.Combine(_pluginsDir, targetName);
        string? best = null;
        foreach (var src in sources)
        {
            if (!File.Exists(src))
                continue;
            if (best is null || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(best))
                best = src;
        }

        if (best is null)
        {
            if (!File.Exists(target))
                throw new InvalidOperationException(
                    $"No {name} plugin found: build it, or put a copy in {_stageDir}/{targetName}");
            return; // already staged, keep it
        }

        if (!File.Exists(target) || File.GetLastWriteTimeUtc(best) > File.GetLastWriteTimeUtc(target))
            File.Copy(best, target, overwrite: true);
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
