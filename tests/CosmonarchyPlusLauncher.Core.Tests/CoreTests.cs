using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using LibGit2Sharp;

namespace CosmonarchyPlusLauncher.Core.Tests;

public class CoreTests
{
    [Fact]
    public void RepoCatalog_default_repo_is_the_github_mirror()
    {
        var repo = RepoCatalog.Find(AppSettings.DefaultRepoId);
        Assert.NotNull(repo);
        Assert.Equal("release", repo.Id);
        Assert.StartsWith("https://github.com/saintromuald/", repo.Url);
        Assert.Equal(RepoKind.Game, repo.Kind);
    }

    [Fact]
    public void RepoCatalog_unofficial_entries_are_marked()
    {
        Assert.All(RepoCatalog.Unofficial, r => Assert.True(r.Unofficial));
        Assert.DoesNotContain(RepoCatalog.Official, r => r.Unofficial);
    }

    [Fact]
    public void RepoCatalog_plugins_have_distinct_stage_names()
    {
        var names = RepoCatalog.Plugins.Select(p => p.BuildDir).ToList();
        Assert.Contains("zz-cosmo-plus.qdp", names);
        Assert.Contains("zz-cosmo-rollback.qdp", names);
    }

    [Fact]
    public void Settings_round_trips_through_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cpl-test-{Guid.NewGuid():N}.json");
        try
        {
            var s = new AppSettings
            {
                RootPath = "/tmp/cosmo",
                SelectedRepoId = "prerelease",
                Variant = Variant.Plus,
                NetcodeRole = NetcodeRole.Solo,
                AccountToken = "secret",
                PatchesInstalled = ["p1", "p2"],
            };
            s.Save(path);

            var loaded = AppSettings.Load(path);
            Assert.Equal("/tmp/cosmo", loaded.RootPath);
            Assert.Equal("prerelease", loaded.SelectedRepoId);
            Assert.Equal(Variant.Plus, loaded.Variant);
            Assert.Equal(NetcodeRole.Solo, loaded.NetcodeRole);
            Assert.Equal("secret", loaded.AccountToken);
            Assert.Equal(["p1", "p2"], loaded.PatchesInstalled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RepoCatalog_my_build_tracks_the_plus_branch()
    {
        Assert.Equal("plus", RepoCatalog.Find("release")!.Branch);
        Assert.Equal("main", RepoCatalog.Find("prerelease")!.Branch);
    }

    [Fact]
    public void CheckoutBranch_moves_head_without_rewriting_other_branches()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cpl-branch-{Guid.NewGuid():N}");
        var remoteDir = Path.Combine(tmp, "remote");
        var cloneDir = Path.Combine(tmp, "clone");
        Directory.CreateDirectory(remoteDir);
        try
        {
            Repository.Init(remoteDir);
            using var seed = new Repository(remoteDir);
            var sig = new Signature("t", "t@example.com", DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(remoteDir, "a.txt"), "a");
            Commands.Stage(seed, "*");
            var mainTip = seed.Commit("A", sig, sig);
            var mainName = seed.Head.FriendlyName;
            var plus = seed.CreateBranch("plus");
            Commands.Checkout(seed, plus);
            File.WriteAllText(Path.Combine(remoteDir, "b.txt"), "b");
            Commands.Stage(seed, "*");
            seed.Commit("B", sig, sig);
            Commands.Checkout(seed, seed.Branches[mainName]); // keep the remote's default branch on main

            Repository.Clone(remoteDir, cloneDir);
            using var mgr = new RepoManager(cloneDir, remoteDir);
            mgr.Fetch();
            mgr.CheckoutBranch("plus", "origin");

            using var clone = new Repository(cloneDir);
            Assert.Equal("plus", clone.Head.FriendlyName);
            Assert.Equal("B", clone.Head.Tip.MessageShort);
            Assert.Equal(mainTip.Sha, clone.Branches[mainName].Tip.Sha);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Settings_save_without_a_path_writes_back_to_the_loaded_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cpl-test-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { FilePath = path, RootPath = "/tmp/a" }.Save();

            var loaded = AppSettings.Load(path);
            Assert.Equal(path, loaded.FilePath);

            loaded.RootPath = "/tmp/b";
            loaded.Save();

            Assert.Equal("/tmp/b", AppSettings.Load(path).RootPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PluginStager_plus_without_a_role_disarms()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cpl-stage-{Guid.NewGuid():N}");
        var build = Path.Combine(tmp, "Release");
        var plugins = Path.Combine(build, "plugins");
        var stage = Path.Combine(tmp, "variants");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "zz-cosmo-plus.qdp"), "plus");
        File.WriteAllText(Path.Combine(stage, "zz-cosmo-rollback.qdp"), "rollback");
        File.WriteAllText(Path.Combine(plugins, "cosmo_rollback.conf"), "stale");

        Environment.SetEnvironmentVariable("CM_ROLLBACK_LIVE", "1");
        Environment.SetEnvironmentVariable("CM_ROLLBACK_SLOT", "0");
        try
        {
            new PluginStager(build, stage, tmp).Apply(Variant.Plus, null);

            Assert.True(File.Exists(Path.Combine(plugins, "zz-cosmo-plus.qdp")));
            Assert.True(File.Exists(Path.Combine(plugins, "zz-cosmo-rollback.qdp")));
            Assert.Null(Environment.GetEnvironmentVariable("CM_ROLLBACK_LIVE"));
            Assert.Null(Environment.GetEnvironmentVariable("CM_ROLLBACK_SLOT"));
            Assert.False(File.Exists(Path.Combine(plugins, "cosmo_rollback.conf")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CM_ROLLBACK_LIVE", null);
            Environment.SetEnvironmentVariable("CM_ROLLBACK_SLOT", null);
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void PluginStager_original_variant_removes_plus_and_disarms()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cpl-stage-{Guid.NewGuid():N}");
        var build = Path.Combine(tmp, "Release");
        var plugins = Path.Combine(build, "plugins");
        var stage = Path.Combine(tmp, "variants");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(plugins, "zz-cosmo-plus.qdp"), "x");

        Environment.SetEnvironmentVariable("CM_ROLLBACK_LIVE", "1");
        Environment.SetEnvironmentVariable("CM_ROLLBACK_SLOT", "0");
        try
        {
            var stager = new PluginStager(build, stage, tmp);
            stager.Apply(Variant.Original, null);

            Assert.False(File.Exists(Path.Combine(plugins, "zz-cosmo-plus.qdp")));
            Assert.Null(Environment.GetEnvironmentVariable("CM_ROLLBACK_LIVE"));
            Assert.Null(Environment.GetEnvironmentVariable("CM_ROLLBACK_SLOT"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CM_ROLLBACK_LIVE", null);
            Environment.SetEnvironmentVariable("CM_ROLLBACK_SLOT", null);
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void PluginStager_stages_plus_from_variants_dir()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cpl-stage-{Guid.NewGuid():N}");
        var build = Path.Combine(tmp, "Release");
        var plugins = Path.Combine(build, "plugins");
        var stage = Path.Combine(tmp, "variants");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "zz-cosmo-plus.qdp"), "dll");
        File.WriteAllText(Path.Combine(stage, "zz-cosmo-rollback.qdp"), "dll");

        try
        {
            var stager = new PluginStager(build, stage, tmp);
            stager.Apply(Variant.Plus, NetcodeRole.Solo);

            Assert.True(File.Exists(Path.Combine(plugins, "zz-cosmo-plus.qdp")));
            Assert.True(File.Exists(Path.Combine(plugins, "zz-cosmo-rollback.qdp")));
            // solo: plus staged, but not armed
            Assert.Null(Environment.GetEnvironmentVariable("CM_ROLLBACK_LIVE"));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void RepoManager_reports_not_installed_for_missing_dir()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cpl-repo-{Guid.NewGuid():N}");
        using var mgr = new RepoManager(tmp, "https://example.com/x.git");
        Assert.False(mgr.IsInstalled());
    }

    [Fact]
    public void DisplayIni_patch_forces_fullscreen_and_preserves_other_keys()
    {
        var ini = string.Join("\n",
            "[ddraw]",
            "width=1280",
            "height=960",
            "fullscreen=true",
            "windowed=true",
            "maintas=true",
            "maxfps=333",
            "vsync=false",
            "shader=Shaders\\nearest-neighbor.glsl",
            "renderer=auto",
            "",
            "; a comment",
            "screenshotdir=.\\Screenshots\\");

        var patched = DisplayIni.Patch(ini);

        Assert.Contains("windowed=false", patched);
        Assert.Contains("fullscreen=true", patched);
        Assert.Contains("width=0", patched);
        Assert.Contains("height=0", patched);
        Assert.DoesNotContain("width=1280", patched);
        Assert.DoesNotContain("height=960", patched);
        Assert.Contains("maxfps=333", patched);
        Assert.Contains("vsync=false", patched);
        Assert.Contains("shader=Shaders\\nearest-neighbor.glsl", patched);
        Assert.Contains("renderer=auto", patched);
        Assert.Contains("screenshotdir=.\\Screenshots\\", patched);
    }

    [Fact]
    public void DisplayIni_patch_handles_windows_line_endings_and_adds_missing_section()
    {
        var patched = DisplayIni.Patch("[sound]\r\nvolume=5\r\n");

        Assert.Contains("[ddraw]", patched);
        Assert.Contains("width=0", patched);
        Assert.Contains("windowed=false", patched);
        Assert.Contains("[sound]", patched);
        Assert.Contains("volume=5", patched);
    }

    [Fact]
    public void DisplayIni_patch_leaves_keys_outside_ddraw_sections_alone()
    {
        var ini = string.Join("\n",
            "[ddraw]",
            "windowed=true",
            "width=800",
            "",
            "windowed=true",
            "",
            "[sound]",
            "volume=3");

        var patched = DisplayIni.Patch(ini);

        // The [ddraw] section is rewritten; the bare key outside any section
        // is not ours to touch (cnc-ddraw config garbage is preserved as-is).
        Assert.Contains("width=0", patched);
        Assert.Contains("volume=3", patched);
        Assert.Equal(2, patched.Split("windowed=").Length - 1);
    }
}
