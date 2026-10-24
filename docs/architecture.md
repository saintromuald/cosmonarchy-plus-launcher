# Architecture

## Layers

```
Avalonia UI (src/CosmonarchyPlusLauncher)
        │  ViewModels (CommunityToolkit.Mvvm)
        ▼
Core (src/CosmonarchyPlusLauncher.Core)  ← all platform logic, no UI deps
        │
        ├── Settings   AppSettings (JSON persistence)
        ├── Services   RepoCatalog, RepoManager, InstallService,
        │              PluginStager, AccountService, CrownLinkService,
        │              PatchManifestService
        └── Models     RepoEntry, Variant, CommitInfo, Patch, User
```

The UI never touches LibGit2Sharp, Octokit, or the filesystem directly; it
drives Core through `IProgress<string>` progress callbacks and `CancellationToken`,
so every page can be unit-tested headless.

## Key decisions

- **net8.0** across projects: matches the ecosystem the game tooling targets
  (GE-Proton/umu scripts assume nothing about us; net8 keeps LibGit2Sharp's
  native binaries happy on both platforms).
- **LibGit2Sharp** for git: same library family as the original, works on
  Linux and Windows. Git operations are synchronous; the UI wraps them in
  `Task.Run`.
- **System.Text.Json** for settings: portable, no registry dependency.
- **Platform split inside Core**: `InstallService` branches on
  `OperatingSystem.IsWindows()` only at the process boundary (launch and the
  Wine registry fix); everything else is shared.
- **Variant staging is a directory contract**: a variant is the set of
  `plugins/*.qdp` files plus environment arming. Identical on Linux and
  Windows; only the process start differs.

## Ports of existing scripts

| Old script | New home |
| --- | --- |
| `omarchy-launch-cosmonarchy*`, `cosmo-variant` | `InstallService` + `PluginStager` + repo entries |
| `myrepo.conf` | `RepoCatalog` (default repo id in `AppSettings`) |
| `cosmo-tempmpq-ensure` | to be ported into `InstallService` (temp.mpq healing) |
| `cosmo-crash-wrap` | optional launch wrapper (planned) |
| `omarchy-launch-cosmonarchy-watch` | Replays page (planned) |

## Clean-room provenance

`docs/behavior.md` is the only artifact derived from the original launcher.
Implementation files never quote or translate decompiled source; they implement
the spec. The commit that first added `docs/behavior.md` records the derivation
(observed behaviour + public endpoints), so the project can answer provenance
questions later.
