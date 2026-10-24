using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;

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
}
