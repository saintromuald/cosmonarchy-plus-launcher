# Cosmonarchy Plus Launcher

An open-source, cross-platform launcher for [Cosmonarchy BW](https://fraudsclub.com/cosmonarchy-bw/).
Install, update, keep any number of versions side by side and switch between
them, apply patches, stage your Plus/rollback variants, link your account, and
play — on Linux (Wine/umu + GE-Proton) and Windows, from one codebase.

This is an **independent, clean-room reimplementation**. It is not affiliated
with the No-Frauds Club and shares no code with the closed-source Fraud
Launcher 2; it only replicates the *behaviour* the community depends on, plus
the build workflow around the maintained mirrors
(`saintromuald/cosmonarchy-bw-release`, `cosmonarchy-plus`,
`cosmonarchy-rollback`).

## Status

Early development. The Core library (repo management, install pipeline,
account client, CrownLink fetch, variant staging, launch) compiles on Linux;
the Avalonia UI is scaffolded but not yet wired.

| Area | State |
| --- | --- |
| Repo install/update + version pick | Working (live-prefix smoke tested) |
| Multiple installed versions (install, pin, switch, delete) | Working (smoke tested) |
| Play + variants (Original/Plus/Netcode) | Working (Linux launch via `umu-run`; Windows path written) |
| Patch pipeline (StarCraft, patches.yml, CrownLink) | Working (smoke tested) |
| Account + community | cosmonarchy.com device login, profile, website and Discord links |
| Replays tab | OG/Plus online lists with per-tab search, race/matchup and team-size filters, follow players; favourites, watched history, private/shared notes with tags, saved and shareable lists; watch any game in one click |
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
