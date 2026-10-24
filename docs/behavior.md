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

Game repos clone into `<RootPath>/<BuildDir>` (Release or Prerelease). Maps
clone into `<RootPath>/Starcraft/maps/CMBW-Root`. The selected repo id and the
"show unofficial repos" flag persist across runs.

## 2. Layout

Windows default root: `C:\Cosmonarchy\`. Linux: the `drive_c/Cosmonarchy`
directory inside `~/Games/cosmonarchy/prefix`.

```
<RootPath>/
  Starcraft/            StarCraft base (StarCraft.exe, ddraw.ini)
    maps/CMBW-Root/     maps repo
    CrownLink.snp       network plugin
  Release/              game repo checkout (Cosmonarchy BW.exe, plugins/)
  starcraft.zip         transient
```

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
4. **Game repo.** Clone if absent (or if the origin URL differs). Otherwise:
   fetch; skip the reset when the tree has local changes; hard-reset to the
   selected version or `origin/main`; update submodules.
5. **Maps.** Clone or fetch + reset `origin/main`.
6. **Wine.** When running under Wine, set `HKCU\Software\Wine\DllOverrides`
   `ddraw = native,builtin`.

Version selection is: Latest (fetch + reset to origin/main), Current (keep the
checked-out commit), or any specific commit from the log. Shallow history is a
user setting; "unshallow" fetches the full log.

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
- **Linux:** `umu-run C:\Cosmonarchy\<BuildDir>\Cosmonarchy BW.exe` with
  `WINEPREFIX`, `PROTONPATH=GE-Proton`, `GAMEID=umu-cosmonarchy-<BuildDir>`,
  `PROTON_VERB=waitforexitandrun`. When an active `HDMI-A-1` exists, disable
  the handheld panel first so Xwayland presents a single 1080p screen.

## 5. Account and social

Device-style login against `https://cosmonarchy.com/`:

1. `GET /api/auth` → `{loginRequestId, authUrl}`.
2. Open `authUrl` in the system browser.
3. Poll `GET /api/auth/status?requestId=...` every 1.5 s until
   `{completed: true, token}`.
4. `GET /api/users/@me` with `Authorization: Bearer <token>` →
   `{discordId, username, email, matchesPlayed, matchesWon, mmr}`.

The token is stored locally (JSON settings; the original uses the registry).
Logout discards it. The social page links to the community site in an embedded
web view.

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
- variant and netcode role
- action on play, full version log
- account token, installed patch ids, CrownLink asset id
- Linux prefix root

## 8. Replays

Planned: list replays from the install directory, filter, and launch the game
with the replay argument (deep-link equivalent).
