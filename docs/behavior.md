# Behaviour specification

Clean-room spec of what the launcher must do, derived from observed behaviour
of Fraud Launcher 2.5.3 (network traffic, registry keys, UI, and decompiled
output used only to enumerate features). The implementation in
`src/CosmonarchyPlusLauncher.Core` is original code written against this spec.

## 1. Repositories

The launcher manages git repositories:

| Id | Role | Source |
| --- | --- | --- |
| release | Game build (default) | `https://github.com/saintromuald/cosmonarchy-bw-release.git` |
| prerelease | Game build | `https://gitlab.com/the-no-frauds-club/cosmonarchy-bw-prerelease.git` |
| release-upstream | Game build fallback | `https://gitlab.com/the-no-frauds-club/cosmonarchy-bw-release.git` |
| luci-* | Unofficial mirrors (hidden unless enabled) | gitea.aromavir.us/lucifirius |
| cmbw-root | Maps | `https://gitlab.com/the-no-frauds-club/cmbw-root.git` |
| plus | Plugin (staged as `zz-cosmo-plus.qdp`) | `https://github.com/saintromuald/cosmonarchy-plus.git` |
| rollback | Plugin (staged as `zz-cosmo-rollback.qdp`) | `https://github.com/saintromuald/cosmonarchy-rollback.git` |

Game repos clone into `<RootPath>/versions/<id>/` for new installs. Legacy
checkouts in `<RootPath>/Release` or `<RootPath>/Prerelease` are adopted as
versions on first launch and keep their directories. Maps clone into
`<RootPath>/Starcraft/maps/CMBW-Root`. The selected repo id, the "show
unofficial repos" flag, the installed version list, and the selected version
persist across runs.

## 2. Layout

Windows default root: `C:\Cosmonarchy\`. Linux: the `drive_c/Cosmonarchy`
directory inside `~/Games/cosmonarchy/prefix`.

```
<RootPath>/
  Starcraft/            StarCraft base (StarCraft.exe, ddraw.ini)
    maps/CMBW-Root/     maps repo
    CrownLink.snp       network plugin
  Release/              legacy game checkout (adopted as a version)
  Prerelease/           legacy game checkout (adopted as a version)
  versions/
    <id>/               a game checkout installed by this launcher
      Cosmonarchy BW.exe, plugins/, mpq/, temp.mpq
  starcraft.zip         transient
```

Every version shares the Starcraft base, the maps repo, the patches, and the
CrownLink plugin; only the game checkout under `versions/<id>` (or a legacy
directory) is per-version, so any number of versions can coexist and switching
is just playing a different one.

## 3. Install / update pipeline

Order of operations, each reported to the UI:

1. **StarCraft base.** If `Starcraft/StarCraft.exe` is missing, download
   `http://fraudsclub.com/files/starcraft.zip`, extract to the root, delete the
   zip, and reset the installed-patch list.
2. **Patches.** Fetch `https://cosmonarchy.com/files/patches/patches.yml`
   (YAML list of `{id, url, filename}`). Apply any whose id is not recorded;
   zip entries extract to the root. Record ids.
3. **CrownLink.** Fetch the latest release of `impromptu1583/SNPJuice` on
   GitHub, pick the `.snp` asset (prefer `crownlink.snp`). Skip when the file
   size matches and the asset id is cached.
4. **Game repo.** Scoped to the selected installed version. Clone into the
   version's own directory when absent (or when the origin URL differs).
   Otherwise: fetch; skip the reset when the tree has local changes; a
   **tracked** version checks out the repo's branch and resets it to
   `<remote>/<branch>`, a **pinned** version hard-resets to its pinned commit;
   update submodules.
5. **Maps.** Clone or fetch + reset `origin/main`.
6. **Wine.** When running under Wine, set `HKCU\Software\Wine\DllOverrides`
   `ddraw = native,builtin`.

Version management: the Play page lists every installed version; Play, Update,
and Delete act on the selected one, and Install always creates a *new* version
(never touching existing checkouts). An install either tracks the repo's
branch (updates to the newest build on play/update) or pins one specific
commit from the log (frozen until removed or explicitly updated).

## 4. Variants and launch

The build's `plugins/*.qdp` set determines the variant:

| Variant | plus qdp | rollback qdp | arming |
| --- | --- | --- | --- |
| Original | absent | present but inert | none |
| Netcode | absent | staged | `CM_ROLLBACK_LIVE=1`, `CM_ROLLBACK_SLOT=0/1` |
| Plus | staged | staged | slot or solo (no arming) |

Plus plugin source order: newest of local release/debug builds or
`variants/zz-cosmo-plus.qdp`. Same rule for the rollback plugin. Stale
`cosmo_rollback.conf` files are removed so arming only ever comes from the
environment.

Plus additionally generates lobby templates (Anti Meta, Draft) from the build's
own `Melee(1).got` when the `cosmonarchy-plus` tools are present, and skips
silently otherwise.

Launch:

- **Windows:** run `Cosmonarchy.exe` (fallback `Cosmonarchy BW.exe`) in the
  repo dir; play waits until a `Starcraft` process with a window title exists.
  After launch: stay open, minimise, or close per the action-on-play setting.
- **Linux:** `umu-run C:\Cosmonarchy\<version dir>\Cosmonarchy BW.exe` with
  `WINEPREFIX`, `PROTONPATH=GE-Proton`, `GAMEID=umu-cosmonarchy-<version id>`,
  `PROTON_VERB=waitforexitandrun`. The launcher returns once the game process
  is started, so versions can be switched while the game is running.

## 5. Account and social

Device-style login against `https://cosmonarchy.com/`:

1. `GET /api/auth` → `{loginRequestId, authUrl}`.
2. Open `authUrl` in the system browser.
3. Poll `GET /api/auth/status?requestId=...` every 1.5 s until
   `{completed: true, token}`.
4. `GET /api/users/@me` with `Authorization: Bearer <token>` →
   `{discordId, username, email, matchesPlayed, matchesWon, mmr}`.

The token is stored locally (JSON settings; the original uses the registry).
Logout discards it. The Community section opens the site and the No-Frauds
Club Discord (`https://discord.gg/s5SKBmY`) in the system browser.

The backend is owned by the No-Frauds Club; this spec covers the client side
only.

## 6. Self-update

The launcher checks a published update feed (the original uses
`https://cosmonarchy.com/files/fl2/update.xml`). Our equivalent: GitHub
Releases of this repository, with version comparison and a mandatory-minimum
field.

## 7. Settings

Persisted in `~/.config/cosmonarchy-plus-launcher/settings.json` (Linux) or
`%APPDATA%\CosmonarchyPlusLauncher\settings.json` (Windows):

- root path, selected repo, show unofficial repos
- installed versions (id, name, repo id, checkout dir, optional pinned
  commit, installed commit) and the selected version id
- variant and netcode role
- action on play, full version log
- account token, installed patch ids, CrownLink asset id
- Linux prefix root

## 8. Replays

Two tabs:

- **Local files** — every `.rep` in the game's replay directory, newest first;
  "Play selected" switches to the vanilla build, copies the file into
  `Maps/Replays`, and launches straight into playback.
- **Online games** — the ReplayKraken list behind cosmonarchy.com/matches
  (`GET https://cosmonarchy.com/api/matches`, downloaded replays at
  `/api/matches/<uuid>/download/<fileId>`). Shows the latest 20 games by
  default; choosing a followed player shows their latest 100. Followed players
  live in the settings (`followedPlayers`). The API returns every match in one
  ~22 MB document with no paging parameters, so the mapped summaries are
  cached in `~/.local/share/cosmonarchy-plus-launcher/matches-cache.json` and
  refreshed on demand or after 10 minutes. "Watch selected" downloads the
  replay into the replay directory and plays it like a local file; "Download
  only" keeps it for later.

Matches carry the short build SHA and channel the replay was recorded on
(`gameVersion`, `gameVersionKind`); the page compares that with the installed
checkout and warns when they differ — a replay only loads on its own build.
Version-pinned switching (CosmoWatch's partial-clone store) is the next step;
for now update or pick a matching version before watching.
