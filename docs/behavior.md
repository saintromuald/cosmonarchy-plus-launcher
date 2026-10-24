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

Tabs: **Cosmonarchy OG**, **Cosmonarchy Plus**, **Local files**, plus
**Favourites** (the local library) and **Community** (the public share board).

### Online lists

The ReplayKraken API behind cosmonarchy.com/matches
(`GET https://cosmonarchy.com/api/matches`; downloads at
`/api/matches/<uuid>/download/<fileId>`). A short SHA reachable from the
maintainer's fork remote is a **Plus** game; everything else is **OG**. Each
tab has its own search (player, map, uploader, version) and follow-player
filter, so the pools stay separate. Latest 20 by default, up to 100 when a
search/follow/filter is active. Followed players persist in settings
(`followedPlayers`). The API has no paging, so the mapped summaries are cached
for 10 minutes in
`~/.local/share/cosmonarchy-plus-launcher/matches-cache-v3.json`.

**Desyncs** — the API flags each replay with `hasDesyncFile`. A match whose
main replay (or any uploaded variant) carries the flag is shown with a `⚠`
badge and the whole row in red, in the OG/Plus lists and on Favourites.

### Filters

- **Race matrix** — Terran/Protoss/Zerg/Askosi/Other on both axes; a ticked
  cell matches when two players of those races are on **opposing** teams.
  Order-independent (`ZvT` = `TvZ`); mirrors are their own cell.
- **Game size** — 1v1, 2v2, 3v3, 4v4, Other (FFA/uneven). Two equal teams only.
- **Ignore observers / AI slots** — drops non-Human slots before counting teams
  and matchups (on by default).
- **Favourites only / Watched only / With notes only**, plus Clear filters.

### Library

Stored in `~/.local/share/cosmonarchy-plus-launcher/library.json`:

- **Watched history** — every Watch records uuid, file id, build and time (list
  markers `▶`, "Watched …" in the notes panel).
- **Favourites** — star toggle (`★` in the list, persisted). Each favourite
  records **when it was added** and a snapshot of the match (players, map,
  build, desync flag) so the Favourites tab works offline. Old libraries
  (version 1, favourites as a plain uuid list) are migrated: the favourites
  are kept, their add date is unknown ("earlier").
- **Notes + tags** — per replay, with visibility **Private** (default) or
  **Shared**. Private notes never appear in exports. Tags are attached to the
  note and only shared with it.
- **Saved lists** — a named snapshot of the filters plus the games they
  matched, with an optional title. Created on the Replays page ("Save
  current") or the Favourites page ("Create list"); games are added from the
  notes panel ("Add to list"). Apply restores the filters, Delete removes it.

### Favourites tab

Lists every favourite newest-first with the add date, a **New/Watched**
split (filter: All / New / Watched) so unwatched replays stand out from
already-watched ones, note badges, and red desync rows. Selecting one opens
the same note editor as the Replays page (text, tags, visibility,
save/delete, remove favourite). Favourites with no snapshot are enriched
from the online list when it refreshes.

### Share and import

Everything shareable is a JSON payload with a `format` field
(`cosmonarchy-plus-launcher/replay-note`, `…/replay-list`,
`…/replay-favorites`). Shared notes/tags ride along; private ones never do.
Payloads carry the sharer's site/Discord name when they are logged in.

- **Share note** — copies a Discord-ready message (note text + the payload in
  a copyable code block) to the clipboard and opens Discord.
- **Share list / favourites** — writes `~/Downloads/Cosmonarchy-<name>.json`
  and copies the payload; in Discord the file is attached (messages are
  length-limited, files are not).
- **Post to Discord** — posts the share straight to a channel through the
  webhook URL from Settings (channel → Integrations → Webhooks). Optional.
- **Import** (Favourites tab) — paste box, clipboard, or file picker. Accepts
  raw JSON, code blocks, or whole Discord messages; merges favourites (first
  add date wins), notes (newer wins), and lists (replace by id).

### Community tab

Public shares live in a public GitHub repo (`communityRepo` in settings,
default `saintromuald/cosmonarchy-shares`):

- `index.json` — the feed: one entry per share (file name, kind, name/title,
  author Discord name, date, game/note counts).
- `shares/cosmonarchy-<random>.json` — the payload files.

The tab lists the index (read-only, no token needed), shows who shared what
and when, and imports any share into the local library. **Make public**
(Favourites tab: favourites, lists, and notes) publishes a payload file and
updates the index; it needs a GitHub token in Settings (fine-grained,
"Contents: read and write" on the repo). Shares carry the sharer's Discord
name when they are logged in on the Account page, "anonymous" otherwise.
**Unpublish** removes your own share (author Discord-id match + token).

### Watch

1. Read `gameVersion` (short SHA) and `gameVersionKind` (Release/Prerelease).
2. Resolve it against the fork and upstream remotes of that channel's checkout.
3. Fetch if needed and check out the exact commit **detached** (no branch
   pointer moves) in `Release`/`Prerelease`.
4. Download the replay into `Starcraft/Maps/Replays`, stage the vanilla
   plugins, and launch `Cosmonarchy BW.exe` with the replay path as its
   argument (playback starts immediately).

A running game blocks Watch with a clear message. Play/Update on the Play page
puts the tracked branch back afterwards. The first launch after a build switch
rebuilds `temp.mpq` (~1–2 minutes, black screen).
