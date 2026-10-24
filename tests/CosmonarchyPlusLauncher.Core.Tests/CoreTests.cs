using CosmonarchyPlusLauncher.Core.Models;
using CosmonarchyPlusLauncher.Core.Services;
using CosmonarchyPlusLauncher.Core.Settings;
using LibGit2Sharp;
using ReleaseAsset = Octokit.ReleaseAsset;

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
    public async Task ReplayKraken_maps_the_api_payload()
    {
        const string json = """
        [
          {
            "_id": "6adc2ae3e3ec4a129c797df4",
            "matchUuid": "d55c1992-bd15-5fdb-8071-1731e6030e0f",
            "gameVersion": "daf2a27",
            "gameVersionKind": "Prerelease",
            "mainReplay": {
              "matchUuid": "d55c1992-bd15-5fdb-8071-1731e6030e0f",
              "fileId": "6adc2ae4e3ec4a129c797df7",
              "gameVersion": "daf2a27",
              "uploaderName": "Esoteric",
              "replayInfo": {
                "mapName": "Lucidity",
                "gameDuration": 921.9,
                "winnerTeam": 0,
                "teams": [
                  { "players": [
                      { "name": "Esoteric", "race": "Zerg", "type": "Human", "apm": 382 },
                      { "name": "Groovy", "race": "Zerg", "type": "Human", "apm": 387 }
                  ] },
                  { "players": null }
                ],
                "uploaderName": "Esoteric"
              }
            }
          }
        ]
        """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var matches = await ReplayKrakenService.ParseMatchesAsync(stream);

        var match = Assert.Single(matches);
        Assert.Equal("Lucidity", match.MapName);
        Assert.Equal(TimeSpan.FromSeconds(921.9), match.Duration);
        Assert.Equal("daf2a27", match.GameVersion);
        Assert.Equal("Prerelease", match.GameVersionKind);
        Assert.Equal("Esoteric", match.UploaderName);
        Assert.Equal("6adc2ae4e3ec4a129c797df7", match.FileId);
        Assert.Equal(["Esoteric", "Groovy"], match.Players.Select(p => p.Name));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(0x6adc2ae3), match.UploadedAt);
    }

    [Fact]
    public void ReplayKraken_filters_by_player_and_limits()
    {
        static OnlineMatch Match(string uuid, string uploader, params string[] players) => new(
            uuid, DateTimeOffset.UnixEpoch, "Map", TimeSpan.FromMinutes(5),
            "daf2a27", "Prerelease",
            players.Select(p => new OnlinePlayer(p, "Zerg", "Human", null)).ToList(),
            uploader, "file-" + uuid);

        var list = new[]
        {
            Match("a", "Groovy", "Groovy", "Esoteric"),
            Match("b", "Someone", "Else", "Other"),
            Match("c", "Groovy", "Third", "Fourth"),
        };

        Assert.Equal(2, ReplayKrakenService.Filter(list, 20, "groovy").Count);
        Assert.Single(ReplayKrakenService.Filter(list, 20, "ESOTERIC"));
        Assert.Equal(2, ReplayKrakenService.Filter(list, 2).Count);
        Assert.Equal(0, ReplayKrakenService.Filter(list, 20, "nobody").Count);
    }

    [Fact]
    public void ReplayKraken_objectid_timestamp_is_hex_seconds()
    {
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(0x6adc2ae3),
            ReplayKrakenService.ObjectIdTime("6adc2ae3e3ec4a129c797df4"));
        Assert.Equal(DateTimeOffset.MinValue, ReplayKrakenService.ObjectIdTime("not-an-id"));
    }

    [Fact]
    public void ReplayQuery_classifies_team_sizes_and_ignores_non_human_slots()
    {
        OnlineMatch Match(params OnlinePlayer[] players) => new(
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "Map", TimeSpan.FromMinutes(5),
            "daf2a27", "Prerelease", players, "uploader", "file");

        var one = Match(
            new OnlinePlayer("a", "Zerg", "Human", 100, 0),
            new OnlinePlayer("b", "Terran", "Human", 100, 1));
        Assert.Equal("1v1", ReplayQuery.TeamSize(one, ignoreObservers: true));

        var two = Match(
            new OnlinePlayer("a", "Zerg", "Human", 100, 0),
            new OnlinePlayer("b", "Terran", "Human", 100, 0),
            new OnlinePlayer("c", "Protoss", "Human", 100, 1),
            new OnlinePlayer("d", "Askosi", "Human", 100, 1));
        Assert.Equal("2v2", ReplayQuery.TeamSize(two, ignoreObservers: true));

        var observed = Match(
            new OnlinePlayer("a", "Zerg", "Human", 100, 0),
            new OnlinePlayer("b", "Terran", "Human", 100, 1),
            new OnlinePlayer("obs", "Protoss", "Computer", 0, 2));
        Assert.Equal("1v1", ReplayQuery.TeamSize(observed, ignoreObservers: true));
        Assert.Equal("Other", ReplayQuery.TeamSize(observed, ignoreObservers: false));
    }

    [Fact]
    public void ReplayQuery_matches_race_pairs_only_across_teams()
    {
        var match = new OnlineMatch(
            "uuid", DateTimeOffset.UtcNow, "Map", TimeSpan.Zero, "daf2a27", "Prerelease",
            [
                new OnlinePlayer("z", "Zerg", "Human", 100, 0),
                new OnlinePlayer("t", "Terran", "Human", 100, 1),
            ],
            "uploader", "file");

        ISet<string> Pairs(params string[] races) =>
            races.Select(r => ReplayQuery.PairKey(r, r)).ToHashSet();

        Assert.True(ReplayQuery.MatchesRacePairs(
            match, new HashSet<string> { ReplayQuery.PairKey("Zerg", "Terran") }, true));
        Assert.True(ReplayQuery.MatchesRacePairs(
            match, new HashSet<string> { ReplayQuery.PairKey("Terran", "Zerg") }, true));
        Assert.False(ReplayQuery.MatchesRacePairs(match, Pairs("Zerg"), true));
        Assert.False(ReplayQuery.MatchesRacePairs(match, Pairs("Zerg", "Terran"), true));
        Assert.True(ReplayQuery.MatchesRacePairs(match, new HashSet<string>(), true));
    }

    [Fact]
    public void ReplayLibrary_round_trips_favorites_notes_and_watched_history()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cpl-library-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ReplayLibraryStore(path);
            var library = new ReplayLibrary();
            Assert.True(library.ToggleFavorite("m1"));
            Assert.True(library.IsFavorite("m1"));
            Assert.False(library.ToggleFavorite("m1"));
            library.MarkWatched("m1", "f1", "daf2a27");
            library.SaveNote("m1", "great game", ["zvp", " finals "], NoteVisibility.Shared);
            store.Save(library);

            var loaded = store.Load();
            Assert.True(loaded.IsFavorite("m1") == false); // toggled back off
            Assert.True(loaded.WasWatched("m1"));
            var note = loaded.NoteFor("m1");
            Assert.NotNull(note);
            Assert.Equal("great game", note!.Text);
            Assert.Equal(["zvp", "finals"], note.Tags);
            Assert.Equal(NoteVisibility.Shared, note.Visibility);

            loaded.SaveNote("m1", "", [], NoteVisibility.Private);
            Assert.Null(loaded.NoteFor("m1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReplaySharing_includes_shared_notes_only()
    {
        var list = new SavedReplayList
        {
            Name = "Best ZvT",
            MatchUuids = ["m1", "m2"],
            Filters = new ReplayFilters
            {
                RacePairs = [ReplayQuery.PairKey("Zerg", "Terran")],
                TeamSizes = ["1v1"],
            },
        };
        var matches = new[]
        {
            new OnlineMatch("m1", DateTimeOffset.UtcNow, "Aegis", TimeSpan.FromMinutes(10),
                "daf2a27", "Prerelease",
                [new OnlinePlayer("a", "Zerg", "Human", 100, 0),
                 new OnlinePlayer("b", "Terran", "Human", 100, 1)],
                "a", "f1"),
            new OnlineMatch("m2", DateTimeOffset.UtcNow, "Lucidity", TimeSpan.FromMinutes(10),
                "daf2a27", "Prerelease",
                [new OnlinePlayer("c", "Zerg", "Human", 100, 0),
                 new OnlinePlayer("d", "Terran", "Human", 100, 1)],
                "c", "f2"),
        };
        var library = new ReplayLibrary();
        library.SaveNote("m1", "watch the muta switch", ["muta"], NoteVisibility.Shared);
        library.SaveNote("m2", "private thought", ["secret"], NoteVisibility.Private);

        var json = ReplaySharing.ExportList(list, matches, library);
        Assert.Contains("watch the muta switch", json);
        Assert.Contains("\"muta\"", json);
        Assert.DoesNotContain("private thought", json);
        Assert.DoesNotContain("secret", json);
        Assert.Contains(ReplayQuery.PairKey("Zerg", "Terran"), json);
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
    public void RepoManager_newest_commit_resolves_head_directly()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cpl-repo-{Guid.NewGuid():N}");
        var remoteDir = Path.Combine(tmp, "remote");
        var cloneDir = Path.Combine(tmp, "clone");
        Directory.CreateDirectory(remoteDir);
        try
        {
            Repository.Init(remoteDir);
            using (var seed = new Repository(remoteDir))
            {
                var sig = new Signature("t", "t@example.com", DateTimeOffset.UtcNow);
                File.WriteAllText(Path.Combine(remoteDir, "a.txt"), "a");
                Commands.Stage(seed, "*");
                seed.Commit("A", sig, sig);
            }
            Repository.Clone(remoteDir, cloneDir);

            using var mgr = new RepoManager(cloneDir, remoteDir);
            var head = mgr.NewestCommit();
            Assert.NotNull(head);
            Assert.Equal(40, head.Sha.Length);
            using var clone = new Repository(cloneDir);
            Assert.Equal(clone.Head.Tip.Sha, head.Sha);
            Assert.Single(mgr.Log());
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
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
            "keytogglefullscreen=0x0D",
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
        // Alt+Enter (0x0D) conflicts with Cosmonarchy; the toggle moves to Alt+F11.
        Assert.Contains("keytogglefullscreen=0x7A", patched);
        Assert.DoesNotContain("keytogglefullscreen=0x0D", patched);
        Assert.Contains("maxfps=333", patched);
        Assert.Contains("vsync=false", patched);
        Assert.Contains("shader=Shaders\\nearest-neighbor.glsl", patched);
        Assert.Contains("renderer=auto", patched);
        Assert.Contains("screenshotdir=.\\Screenshots\\", patched);
    }

    [Fact]
    public void DisplayIni_patch_adds_missing_toggle_key()
    {
        // No keytogglefullscreen: cnc-ddraw would fall back to Alt+Enter.
        var patched = DisplayIni.Patch("[ddraw]\nwidth=800\nheight=600\n");

        Assert.Contains("keytogglefullscreen=0x7A", patched);
        Assert.Contains("width=0", patched);
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

    [Fact]
    public void Settings_round_trips_version_list()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cpl-test-{Guid.NewGuid():N}.json");
        try
        {
            var s = new AppSettings
            {
                RootPath = "/tmp/cosmo",
                Versions =
                [
                    new GameVersion { Id = "a", Name = "A", RepoId = "release", Dir = "versions/a", PinnedSha = "1234567890abcdef" },
                    new GameVersion { Id = "b", Name = "B", RepoId = "prerelease", Dir = "Prerelease" },
                ],
                SelectedVersionId = "a",
            };
            s.Save(path);

            var loaded = AppSettings.Load(path);
            Assert.Equal(2, loaded.Versions.Count);
            Assert.False(loaded.Versions[0].TracksBranch);
            Assert.Equal("1234567890abcdef", loaded.Versions[0].PinnedSha);
            Assert.True(loaded.Versions[1].TracksBranch);
            Assert.Equal("a", loaded.SelectedVersionId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VersionManager_adopts_legacy_and_versions_checkouts()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cpl-versions-{Guid.NewGuid():N}");
        var gameSrc = Path.Combine(tmp, "game-src");
        var root = Path.Combine(tmp, "install");
        try
        {
            Directory.CreateDirectory(gameSrc);
            Repository.Init(gameSrc);
            using (var seed = new Repository(gameSrc))
            {
                var sig = new Signature("t", "t@example.com", DateTimeOffset.UtcNow);
                File.WriteAllText(Path.Combine(gameSrc, "a.txt"), "a");
                Commands.Stage(seed, "*");
                seed.Commit("A", sig, sig);
            }

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "versions"));
            Repository.Clone(gameSrc, Path.Combine(root, "Release"));
            Repository.Clone(gameSrc, Path.Combine(root, "versions", "old-build"));

            var entry = new RepoEntry("test-game", "Test Game", gameSrc, RepoKind.Game, BuildDir: "Release");
            var settings = new AppSettings
            {
                RootPath = root,
                FilePath = Path.Combine(tmp, "settings.json"),
            };
            var manager = new VersionManager(settings);

            var adopted = manager.AdoptExistingInstallations(known: [entry]);

            Assert.Equal(2, adopted);
            var legacy = manager.Find("release");
            Assert.NotNull(legacy);
            Assert.Equal("Release", legacy.Dir);
            Assert.Equal("test-game", legacy.RepoId);
            Assert.Equal("Test Game", legacy.Name);
            Assert.Null(legacy.PinnedSha);        // adopted versions track the branch
            Assert.NotNull(legacy.InstalledSha);  // head is recorded for display
            Assert.Equal(40, legacy.InstalledSha.Length);

            var extra = manager.Find("old-build");
            Assert.NotNull(extra);
            Assert.Equal("versions/old-build", extra.Dir);
            Assert.Equal("Old Build", extra.Name);

            // A second scan must not duplicate anything.
            Assert.Equal(0, manager.AdoptExistingInstallations(known: [entry]));
            Assert.True(settings.Versions.Count == 2, "re-scan must not duplicate versions");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void VersionManager_create_new_uses_unique_slugs_and_default_names()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cpl-test-{Guid.NewGuid():N}.json");
        try
        {
            var entry = new RepoEntry("test-game", "Test Game",
                "https://example.com/g.git", RepoKind.Game, BuildDir: "Release");
            var settings = new AppSettings { RootPath = "/tmp/cosmo", FilePath = path };
            var manager = new VersionManager(settings);

            var a = manager.CreateNew(entry, null, "My Cool Build!");
            Assert.Equal("my-cool-build", a.Id);
            Assert.Equal("versions/my-cool-build", a.Dir);
            Assert.Equal("My Cool Build!", a.Name);
            Assert.True(a.TracksBranch);

            settings.Versions.Add(a);
            var b = manager.CreateNew(entry, null, "My Cool Build!");
            Assert.Equal("my-cool-build-2", b.Id);
            Assert.Equal("versions/my-cool-build-2", b.Dir);

            var c = manager.CreateNew(entry, "abcdef1234567890", null);
            Assert.Equal("Test Game — abcdef12", c.Name);
            Assert.Equal("test-game-abcdef12", c.Id);
            Assert.False(c.TracksBranch);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VersionManager_remove_never_deletes_outside_the_root()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cpl-remove-{Guid.NewGuid():N}");
        var root = Path.Combine(tmp, "install");
        var outside = Path.Combine(tmp, "outside");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");

            var inside = new GameVersion { Id = "in", Name = "Inside", RepoId = "release", Dir = Path.Combine("versions", "in") };
            Directory.CreateDirectory(Path.Combine(root, "versions", "in"));
            var escape = new GameVersion { Id = "out", Name = "Outside", RepoId = "release", Dir = Path.Combine("..", "outside") };
            var settings = new AppSettings
            {
                RootPath = root,
                Versions = [inside, escape],
                FilePath = Path.Combine(tmp, "settings.json"),
            };
            var manager = new VersionManager(settings);

            manager.Remove(escape); // must NOT delete anything outside the root
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));

            manager.Remove(inside); // deletes versions/in
            Assert.False(Directory.Exists(Path.Combine(root, "versions", "in")));
            Assert.Empty(settings.Versions);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void GameVersion_summary_shows_pin_and_repo()
    {
        var pinned = new GameVersion { Name = "X", RepoId = "release", PinnedSha = "1234567890abcdef" };
        Assert.Contains("pinned 12345678", pinned.Summary);
        Assert.Contains("Release (My Build)", pinned.Summary);

        var tracked = new GameVersion { Name = "X", RepoId = "release" };
        Assert.Contains("tracks plus", tracked.Summary);
        Assert.Contains("auto-updates", tracked.Summary);
    }

    [Fact]
    public async Task ReplayKraken_maps_the_desync_flag()
    {
        const string json = """
        [
          {
            "_id": "6adc2ae3e3ec4a129c797df4",
            "matchUuid": "d55c1992-bd15-5fdb-8071-1731e6030e0f",
            "gameVersion": "daf2a27",
            "gameVersionKind": "Prerelease",
            "mainReplay": {
              "matchUuid": "d55c1992-bd15-5fdb-8071-1731e6030e0f",
              "fileId": "6adc2ae4e3ec4a129c797df7",
              "hasDesyncFile": true,
              "gameVersion": "daf2a27",
              "uploaderName": "Esoteric",
              "replayInfo": { "mapName": "Lucidity", "gameDuration": 300 }
            }
          }
        ]
        """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var match = Assert.Single(await ReplayKrakenService.ParseMatchesAsync(stream));
        Assert.True(match.HasDesync);
    }

    [Fact]
    public void ReplayLibrary_favourites_record_add_dates_and_snapshots()
    {
        var library = new ReplayLibrary();
        var snapshot = new ReplaySnapshot { MapName = "Aegis", PlayersLabel = "A vs B", HasDesync = true };
        Assert.True(library.ToggleFavorite("m1", snapshot));
        var favorite = library.FavoriteFor("m1");
        Assert.NotNull(favorite);
        Assert.NotNull(favorite!.Snapshot);
        Assert.Equal("Aegis", favorite.Snapshot!.MapName);
        Assert.True(favorite.Snapshot.HasDesync);
        Assert.True((DateTimeOffset.UtcNow - favorite.AddedAt) < TimeSpan.FromMinutes(1));
        Assert.False(library.ToggleFavorite("m1", snapshot));
        Assert.Null(library.FavoriteFor("m1"));
    }

    [Fact]
    public void ReplayLibraryStore_migrates_v1_favourite_uuids()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cpl-library-v1-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 1,
                  "Watched": [{ "MatchUuid": "w1", "FileId": "f1", "Version": "v" }],
                  "Favorites": ["m1", "m2"],
                  "Notes": [{ "MatchUuid": "m1", "Text": "hi", "Tags": [], "Visibility": 1 }],
                  "Lists": []
                }
                """);

            var loaded = new ReplayLibraryStore(path).Load();

            Assert.True(loaded.IsFavorite("m1"));
            Assert.True(loaded.IsFavorite("m2"));
            Assert.Equal(DateTimeOffset.MinValue, loaded.FavoriteFor("m1")!.AddedAt);
            Assert.Null(loaded.FavoriteFor("m1")!.Snapshot);
            Assert.True(loaded.WasWatched("w1"));
            Assert.NotNull(loaded.NoteFor("m1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReplaySharing_note_payload_round_trips_through_a_discord_message()
    {
        var match = new OnlineMatch(
            "uuid-1", DateTimeOffset.UtcNow, "Lucidity", TimeSpan.FromMinutes(10),
            "daf2a27", "Prerelease",
            [new OnlinePlayer("a", "Zerg", "Human", 100, 0)],
            "a", "f1", HasDesync: true);
        var library = new ReplayLibrary();
        library.ToggleFavorite(match.MatchUuid, ReplaySnapshot.From(match));
        library.SaveNote(match.MatchUuid, "desynced here", ["desync"], NoteVisibility.Shared);
        var note = library.NoteFor(match.MatchUuid)!;

        var payload = ReplaySharing.ExportNotePayload(
            note, match, library.FavoriteFor(match.MatchUuid),
            new ReplaySharing.Author("saintromuald", "discord-42"));
        var message = ReplaySharing.DiscordNoteMessage(payload, note, match);
        Assert.True(message.Length <= ReplaySharing.DiscordMaxLength);
        Assert.Contains("desynced here", message);
        Assert.Contains("saintromuald", payload);

        var imported = ReplaySharing.ParseImport(message);
        Assert.NotNull(imported);
        Assert.Equal("saintromuald", imported!.AuthorDiscordName);
        var favorite = Assert.Single(imported.Favorites);
        Assert.Equal("uuid-1", favorite.MatchUuid);
        Assert.True(favorite.Snapshot!.HasDesync);
        var importedNote = Assert.Single(imported.Notes);
        Assert.Equal("desynced here", importedNote.Text);
        Assert.Equal(["desync"], importedNote.Tags);
    }

    [Fact]
    public void ReplaySharing_list_export_carries_title_favourites_and_desync()
    {
        var match = new OnlineMatch(
            "uuid-9", DateTimeOffset.UtcNow, "Aegis", TimeSpan.FromMinutes(5),
            "daf2a27", "Prerelease",
            [new OnlinePlayer("a", "Zerg", "Human", 100, 0),
             new OnlinePlayer("b", "Terran", "Human", 100, 1)],
            "a", "f9", HasDesync: true);
        var library = new ReplayLibrary();
        library.ToggleFavorite(match.MatchUuid, ReplaySnapshot.From(match));
        library.SaveNote(match.MatchUuid, "check the bunker rush", [], NoteVisibility.Shared);
        var list = new SavedReplayList { Name = "Best ZvT", Title = "finals only", MatchUuids = [match.MatchUuid] };

        var json = ReplaySharing.ExportList(list, [match], library);

        Assert.Contains("\"title\": \"finals only\"", json);
        Assert.Contains("\"desynced\": true", json);
        var imported = ReplaySharing.ParseImport(json);
        Assert.NotNull(imported);
        var importedList = Assert.Single(imported!.Lists);
        Assert.Equal("Best ZvT", importedList.Name);
        Assert.Equal("finals only", importedList.Title);
        Assert.True(imported.Favorites.Any(f => f.MatchUuid == "uuid-9"));
    }

    [Fact]
    public void ReplaySharing_import_rejects_unrecognised_text()
    {
        Assert.Null(ReplaySharing.ParseImport("hello there"));
        Assert.Null(ReplaySharing.ParseImport("{ \"format\": \"something-else\", \"version\": 1 }"));
    }

    [Fact]
    public void ReplayLibrary_merge_keeps_newer_notes_and_deduplicates_favourites()
    {
        var library = new ReplayLibrary();
        library.SaveNote("m1", "old text", [], NoteVisibility.Shared);
        library.NoteFor("m1")!.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var imported = new ImportedShare
        {
            AuthorDiscordName = "someone",
        };
        imported.Notes.Add(new ReplayNote
        {
            MatchUuid = "m1",
            Text = "new text",
            Visibility = NoteVisibility.Shared,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        imported.Favorites.Add(new FavoriteReplay { MatchUuid = "m1", AddedAt = DateTimeOffset.UtcNow });

        var (favourites, notes, updated, lists) = library.Merge(imported);

        Assert.Equal(1, favourites);
        Assert.Equal(0, notes);
        Assert.Equal(1, updated);
        Assert.Equal(0, lists);
        Assert.Equal("new text", library.NoteFor("m1")!.Text);
    }

    [Fact]
    public void CommunityService_index_round_trips()
    {
        var shares = new[]
        {
            new CommunityShare
            {
                FileName = "cosmonarchy-abc12345.json",
                Kind = ReplaySharing.FavoritesFormat,
                Name = "Favourites",
                DiscordName = "saintromuald",
                DiscordId = "42",
                SavedAt = DateTimeOffset.UtcNow,
                GameCount = 7,
                NoteCount = 2,
            },
            new CommunityShare
            {
                FileName = "cosmonarchy-def67890.json",
                Kind = ReplaySharing.ListFormat,
                Name = "Best ZvT",
                Title = "finals",
                SavedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                GameCount = 3,
            },
        };

        var json = CommunityService.SerializeIndex(shares);
        var parsed = CommunityService.ParseIndex(json);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Favourites", parsed[0].Name); // newest first
        Assert.Equal("saintromuald", parsed[0].DiscordName);
        Assert.Equal("anonymous", parsed[1].AuthorLabel);
        Assert.Equal(ReplaySharing.ListFormat, parsed[1].Kind);
        Assert.Equal("finals", parsed[1].Title);
    }

    [Fact]
    public void DiscordWebhook_url_validation()
    {
        Assert.True(DiscordWebhook.IsValidWebhookUrl("https://discord.com/api/webhooks/123/abc"));
        Assert.True(DiscordWebhook.IsValidWebhookUrl("https://discordapp.com/api/webhooks/123/abc"));
        Assert.False(DiscordWebhook.IsValidWebhookUrl("https://example.com/api/webhooks/123/abc"));
        Assert.False(DiscordWebhook.IsValidWebhookUrl(""));
        Assert.False(DiscordWebhook.IsValidWebhookUrl(null));
    }

    [Fact]
    public void UpdateService_compares_versions_numerically()
    {
        Assert.True(UpdateService.CompareVersions("0.3.0", "0.2.0") > 0);
        Assert.True(UpdateService.CompareVersions("0.10.0", "0.9.9") > 0);
        Assert.True(UpdateService.CompareVersions("0.2.0", "0.2.0") == 0);
        Assert.True(UpdateService.CompareVersions("0.1.9", "0.2.0") < 0);
        Assert.True(UpdateService.CompareVersions("0.3.0-beta1", "0.3.0") == 0);
    }

    [Fact]
    public void UpdateService_picks_the_platform_asset()
    {
        static ReleaseAsset Asset(string name) => new(
            "https://example.com/" + name, 1, "node", name, "label", "uploaded",
            "https://example.com/" + name, 1024, 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "application/octet-stream", null);

        var assets = new[]
        {
            Asset("CosmonarchyPlusLauncher-linux-x64.tar.gz"),
            Asset("CosmonarchyPlusLauncher-win-x64.zip"),
            Asset("CosmonarchyPlusLauncher-x86_64.AppImage"),
        };

        Assert.Equal("CosmonarchyPlusLauncher-linux-x64.tar.gz",
            UpdateService.PickAsset(assets, windows: false)!.Name);
        Assert.Equal("CosmonarchyPlusLauncher-win-x64.zip",
            UpdateService.PickAsset(assets, windows: true)!.Name);
        Assert.Null(UpdateService.PickAsset([], windows: false));
        Assert.Null(UpdateService.PickAsset([Asset("CosmonarchyPlusLauncher-x86_64.AppImage")], windows: false));
    }

    [Fact]
    public void SelfUpdater_detects_the_linux_directory_install()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cpl-kind-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "CosmonarchyPlusLauncher"), "");
            Assert.Equal(InstallKind.Other, SelfUpdater.GetInstallKind(dir));

            File.WriteAllText(Path.Combine(dir, "CosmonarchyPlusLauncher.deps.json"), "{}");
            Assert.Equal(InstallKind.LinuxDirectory, SelfUpdater.GetInstallKind(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AccountService_fixes_the_staging_auth_redirect()
    {
        const string url =
            "https://discord.com/api/oauth2/authorize?client_id=1365001746597941350" +
            "&redirect_uri=https%3A%2F%2Fstars.veeq7.com%2Fapi%2Fauth%2Fcallback" +
            "&response_type=code&scope=identify%20email&state=4e2dff0f-ec78-4290";

        var fixedUrl = AccountService.FixAuthUrl(url);

        Assert.Contains("redirect_uri=https%3A%2F%2Fcosmonarchy.com%2Fapi%2Fauth%2Fcallback", fixedUrl);
        Assert.Contains("state=4e2dff0f-ec78-4290", fixedUrl);
        Assert.Contains("scope=identify%20email", fixedUrl);
        Assert.DoesNotContain("stars.veeq7.com", fixedUrl);

        // A production redirect (and any other part of the URL) is untouched.
        Assert.Equal(
            url.Replace("stars.veeq7.com", "cosmonarchy.com"),
            AccountService.FixAuthUrl(url.Replace("stars.veeq7.com", "cosmonarchy.com")));

        // Non-Discord URLs and garbage pass through.
        Assert.Equal("https://example.com/x", AccountService.FixAuthUrl("https://example.com/x"));
        Assert.Equal("not a url", AccountService.FixAuthUrl("not a url"));
    }

    [Fact]
    public async Task SelfUpdater_refuses_to_prepare_for_unmanaged_installs()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cpl-noinst-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var info = new UpdateInfo("v9.9.9", "Nine", "https://example.com", "", true, null, null);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new SelfUpdater().PrepareAsync(dir, info));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SomeFutureKind")]
    public void ReplayBuildResolver_original_kind_means_no_usable_channel(string kind) =>
        Assert.True(ReplayBuildResolver.IsOriginal(kind));

    [Theory]
    [InlineData("Release")]
    [InlineData("release")]
    [InlineData("Prerelease")]
    [InlineData("PRERELEASE")]
    public void ReplayBuildResolver_known_kinds_are_not_original(string kind) =>
        Assert.False(ReplayBuildResolver.IsOriginal(kind));

    [Fact]
    public void ReplayBuildResolver_prerelease_matches_case_insensitively()
    {
        Assert.True(ReplayBuildResolver.IsPrerelease("Prerelease"));
        Assert.True(ReplayBuildResolver.IsPrerelease("prerelease"));
        Assert.False(ReplayBuildResolver.IsPrerelease("Release"));
        Assert.False(ReplayBuildResolver.IsPrerelease(""));
    }

    [Fact]
    public void RepoCatalog_carries_every_original_source()
    {
        // The sources Fraud Launcher 2's backdate list can reach: the
        // original (pre-split) repository plus its forebears.
        var original = ReplayBuildResolver.OriginalRepo();
        Assert.NotNull(original);
        Assert.Equal("https://gitlab.com/the-no-frauds-club/cosmonarchy.git", original.Url);
        Assert.Equal("master", original.Branch);
        Assert.Equal(RepoKind.Game, original.Kind);

        var oldRelease = ReplayBuildResolver.OldReleaseRepo();
        Assert.NotNull(oldRelease);
        Assert.Equal("https://gitlab.com/the-no-frauds-club/release/Cosmonarchy-old-1.git", oldRelease.Url);
        Assert.Equal("master", oldRelease.Branch);

        var nightly = ReplayBuildResolver.NightlyRepo();
        Assert.NotNull(nightly);
        Assert.Equal("https://gitlab.com/Pr0nogo3/specialbus.git", nightly.Url);

        // And they are all offered on the Play page like the other official repos.
        Assert.Contains(RepoCatalog.Official, r => r.Id == "original");
        Assert.Contains(RepoCatalog.Official, r => r.Id == "old-release");
        Assert.Contains(RepoCatalog.Official, r => r.Id == "nightly");
    }

    [Fact]
    public void Version_labels_drop_the_empty_original_kind()
    {
        var match = new OnlineMatch(
            "u", DateTimeOffset.UtcNow, "Map", TimeSpan.FromMinutes(5),
            "daf2a27", "", [], "uploader", "file");
        Assert.Equal("daf2a27", match.VersionLabel);

        var snapshot = new ReplaySnapshot { GameVersion = "daf2a27", GameVersionKind = "" };
        Assert.Equal("daf2a27", snapshot.VersionLabel);

        var prerelease = new OnlineMatch(
            "u", DateTimeOffset.UtcNow, "Map", TimeSpan.FromMinutes(5),
            "daf2a27", "Prerelease", [], "uploader", "file");
        Assert.Equal("daf2a27 (Prerelease)", prerelease.VersionLabel);
    }
}
