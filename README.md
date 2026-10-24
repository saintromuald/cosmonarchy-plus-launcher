# Cosmonarchy Plus Launcher

An open-source, cross-platform launcher for [Cosmonarchy BW](https://fraudsclub.com/cosmonarchy-bw/).
Install, update, pick any version, apply patches, stage your Plus/rollback
variants, link your account, and play — on Linux (Wine/umu + GE-Proton) and
Windows, from one codebase.

This is an **independent, clean-room reimplementation**. It is not affiliated
with the No-Frauds Club and shares no code with the closed-source Fraud
Launcher 2; it only replicates the *behaviour* the community depends on, plus
the workflow around the user's own builds (`saintromuald/cosmonarchy-bw-release`,
`cosmonarchy-plus`, `cosmonarchy-rollback`).

## Status

Early development. The Core library (repo management, install pipeline,
account client, CrownLink fetch, variant staging, launch) compiles on Linux;
the Avalonia UI is scaffolded but not yet wired.

| Area | State |
| --- | --- |
| Repo install/update + version pick | Working (live-prefix smoke tested) |
| Play + variants (Original/Plus/Netcode) | Working (Linux launch via `umu-run`; Windows path written) |
| Patch pipeline (StarCraft, patches.yml, CrownLink) | Working (smoke tested) |
| Discord link + social page | Working client + Account page |
| Replays tab | Lists + opens folder (deep-link launch later) |
| Self-updater | GitHub Releases check in Settings |
| Packaging | CI builds win-x64 exe, linux-x64, AppImage; Arch PKGBUILD |

## Build

```bash
dotnet build
```

Run (Linux, needs `umu`/GE-Proton and an existing `~/Games/cosmonarchy` prefix):

```bash
dotnet run --project src/CosmonarchyPlusLauncher
```

## Design

- `src/CosmonarchyPlusLauncher.Core` — platform logic, no UI, fully testable.
- `src/CosmonarchyPlusLauncher` — Avalonia MVVM UI.
- `tests/` — xunit tests for the Core.

Behaviour is specified in [`docs/behavior.md`](docs/behavior.md) — written from
observed behaviour of the original launcher, kept separate from the
implementation so provenance is clear.

## Licence

MIT. The upstream game, its repositories, the `cosmonarchy.com` backend, and
the CrownLink/SNP assets belong to their respective owners.
