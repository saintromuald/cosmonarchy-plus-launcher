# Behaviour specification

Clean-room spec of what the launcher must do, derived from observed behaviour
of Fraud Launcher 2.5.3 (network traffic, registry keys, UI, and decompiled
output used only to enumerate features). The implementation in
`src/CosmonarchyPlusLauncher.Core` is original code written against this spec.

## 1. Repositories

The launcher manages git repositories, grouped into the three variant families
the Play page filters on:

| Id | Family | Role | Source |
| --- | --- | --- | --- |
| release-upstream | Original | Stable game build (upstream release; there is no separate stable repo) | `https://gitlab.com/the-no-frauds-club/cosmonarchy-bw-release.git` |
| prerelease | Original | Prerelease game build | `https://gitlab.com/the-no-frauds-club/cosmonarchy-bw-prerelease.git` |
| original | Original | Original (pre-split) game build; replay fallback for games the original client recorded without a channel | `https://gitlab.com/the-no-frauds-club/cosmonarchy.git` |
| old-release | Original | 2023 old release archive (backdate source) | `https://gitlab.com/the-no-frauds-club/release/Cosmonarchy-old-1.git` |
| nightly | Original | 2023 nightly archive (backdate source) | `https://gitlab.com/Pr0nogo3/specialbus.git` |
| luci-* | Original | Unofficial mirrors (hidden unless enabled) | gitea.aromavir.us/lucifirius |
| release | Plus | Plus-enabled game build (the mirror's `plus` branch) | `https://github.com/saintromuald/cosmonarchy-bw-release.git` |
| prerelease-mine | Plus | Plus-enabled prerelease build (`plus` branch) | `https://github.com/saintromuald/cosmonarchy-bw-prerelease.git` |
| plus | Plus | Plugin package `zz-cosmo-plus.qdp` (GitHub release zip) | `https://github.com/saintromuald/cosmonarchy-plus.git` |
| rollback | Netcode | Plugin package `zz-cosmo-rollback.qdp` (repo + local build) | `https://github.com/saintromuald/cosmonarchy-rollback.git` |
| cmbw-root | — | Maps | `https://gitlab.com/the-no-frauds-club/cmbw-root.git` |

Stable is the upstream release channel; the list never shows a Stable row for
a repository that does not exist. Netcode has no builds of its own: it offers
the Plus-enabled builds (rollback runs on them) plus the rollback plugin.

Game repos clone into `<RootPath>/versions/<id>/` for new installs. Legacy
checkouts in `<RootPath>/Release` or `<RootPath>/Prerelease` are adopted as
versions on first launch and keep their directories. Maps clone into
`<RootPath>/Starcraft/maps/CMBW-Root`. Plugin repos install into the shared
`variants/` folder (plus a checkout under `<RootPath>/plugins/` when the
plugin is fetched from git). The selected repo id, the "show unofficial
repos" flag, the installed version and plugin lists, and the selected version
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
  Original/             original-repo checkout used for replay playback of
                        games recorded without a channel (never a Play version)
  versions/
    <id>/               a game checkout installed by this launcher
      Cosmonarchy BW.exe, plugins/, mpq/, temp.mpq
  plugins/
    rollback/           checkout of a plugin repo (when installed from git)
  starcraft.zip         transient

<prefix root>/variants/  staged .qdp plugin builds (Linux; Windows:
                         <RootPath>/variants/)
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
   version's own directory when absent. Immediately after the clone a tracked
   version checks out the entry's branch — `plus` for the Plus mirror builds,
   `master` for the archives, `main` otherwise — so "Latest" never leaves the
   checkout on the remote's default snapshot. Otherwise: fetch; skip the reset
   when the tree has local changes; a **tracked** version checks out the repo's
   branch and resets it to `<remote>/<branch>`, a **pinned** version
   hard-resets to its pinned commit; update submodules.
5. **Maps.** Clone or fetch + reset `origin/main`.
6. **Wine.** When running under Wine, set `HKCU\Software\Wine\DllOverrides`
   `ddraw = native,builtin`.

### Download progress

Every download reports throttled progress (twice a second or every 512 KiB,
plus a final report) as `Label… N% (X MB / Y MB)`. The Play page, both online
Replays tabs, the Local files tab, and the Settings update flow show a
determinate progress bar driven by that percentage; git clones/fetches (which
cannot know their size up front) report bytes + objects instead
(`Fetching <url>… 128 MB · 2,041 / 4,512 objects`) and show an indeterminate
bar. Responses that are gzip-encoded or carry no content length report bytes
only — a percentage against the compressed size would be wrong. The git
transfer callback must return **true** to keep going; returning false cancels
the clone with `indexer progress callback returned -7`, and only remote clones
(rather than local file clones) hit it.

Version management: the Play page lists the installed versions **of the
selected variant family**; Play, Update, and Delete act on the selected one,
and Install always creates a *new* version (never touching existing
checkouts). A version record is written before the download starts, so the
row appears immediately with "not downloaded yet"; a failed clone keeps the
record and Update retries it. Repeats of the same default name get
" (2)", " (3)", … so two installs of one repository never look identical. An
install either tracks the repo's branch (updates to the newest build on
play/update) or pins one specific commit from the log (frozen until removed or
explicitly updated).

Plugin installs are separate rows in the same filtered list:

- **Plus** downloads the newest `CosmonarchyPlus-plugins-*.zip` GitHub release
  asset and extracts its .qdp files (Plus and rollback, version-matched).
- **Netcode** clones/fetches the rollback repository, checks out its
  `cosmonarchy-plus` branch, and copies the built `zz-cosmo-rollback.qdp` from
  the checkout, the local project build, or an already-staged copy. It fails
  with a clear message when none exists.
- Each installed plugin records the files and their SHA-256. Remove deletes a
  file only when this record wrote the copy now on disk and no other plugin
  package still needs it.

## 4. Variants and launch

The Play page's bottom **Variant** dropdown is the master switch:

| Filter | Installable repositories | Installed list | Staging on play |
| --- | --- | --- | --- |
| Original | Stable (upstream release), Prerelease, Original/old-release/nightly archives, unofficial mirrors | Original builds | no plugin; rollback qdp inert |
| Plus | Plus mirror builds (plus branch), Plus plugin package | Plus builds and the Plus plugin | Plus + rollback staged; **Solo by default** (disarmed); Host/Guest arms the rollback turn system |
| Netcode | Plus mirror builds, Rollback plugin package | Netcode builds and the rollback plugin | rollback armed; host/guest only |

Each installed row carries a coloured ORIGINAL / PLUS / NETCODE badge and a
Build/Plugin label. The variant stored on a version decides its staging, so
pressing Play on an Original install never picks up a Plus plugin because the
filter was left on Plus. Within one browser session the variant remembers the
repository last picked in it. Each family keeps its own netcode role (Plus
role defaults to Solo; Netcode to Host), and switching filters never changes
another family's role — the armed turn system takes over the mouse and
keyboard, so normal Plus play must never arm it accidentally. The Play page
shows a warning whenever the chosen role would arm.

The build's `plugins/*.qdp` set determines what the game loads:

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

The Settings page checks `saintromuald/cosmonarchy-plus-launcher` releases
and, when a newer version exists, offers a one-click **Install update**:

- **Linux directory install** (self-contained publish in
  `~/.local/opt/cosmonarchy-plus-launcher`): downloads
  `CosmonarchyPlusLauncher-linux-x64.tar.gz`, verifies it, extracts it to
  `.cosmonarchy-plus-launcher-update/staging`, then runs a detached apply
  script that waits for the app to exit, swaps the directories (keeping the
  previous version as `.cosmonarchy-plus-launcher-backup`) and relaunches.
- **Windows single-file exe**: same flow with the `win-x64.zip` asset and a
  `cmd` apply script that replaces the exe after the process ends.
- **AppImage / package installs**: the launcher cannot replace itself; the
  update is offered through the release page instead.

A failed swap restores the old directory; a broken update never touches the
install (staging is verified first). Version bumps land in the csproj and the
Arch PKGBUILD together.

## 7. Settings

Persisted in `~/.config/cosmonarchy-plus-launcher/settings.json` (Linux) or
`%APPDATA%\CosmonarchyPlusLauncher\settings.json` (Windows):

- root path, selected repo, show unofficial repos
- installed versions (id, name, repo id, checkout dir, optional pinned
  commit, installed commit, **variant family**) and the selected version id
- installed plugins (id, name, repo id, variant, release tag/commit, the
  files with their SHA-256, optional checkout dir)
- variant filter and netcode role
- action on play, full version log
- account token, installed patch ids, CrownLink asset id
- Linux prefix root

Records written before the variant field existed have no family stored; they
inherit their repository's family (`release` → Plus, `prerelease` → Original)
and are not rewritten unless another change saves the file.

## 8. Replays

Tabs: **Cosmonarchy OG**, **Cosmonarchy Plus**, **Local files**,
**Following**, plus **Favourites** (the local library) and **Community** (the
public share board).

### Online lists

The ReplayKraken API behind cosmonarchy.com/matches
(`GET https://cosmonarchy.com/api/matches`; downloads at
`/api/matches/<uuid>/download/<fileId>`). A short SHA reachable from the
maintainer's fork remote is a **Plus** game; everything else is **OG**. Each
tab has its own search (player, map, uploader, version) and follow-player
filter, so the pools stay separate. Latest 20 by default, up to 100 when a
search/follow/filter is active; a **Show more** button grows the list further
(200 at a time) until everything is shown. Followed players persist in
settings (`followedPlayers`). The API has no paging, so the mapped summaries
are cached for 10 minutes in
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

### Following tab

Everyone followed, with a per-channel split: each player lists their OG and
Plus game counts and a "OG / Plus / OG + Plus / no games" marker. Selecting a
player shows all of their games from **both channels** in one list, each row
badged with its channel, with the usual favourite/watched/note/desync badges,
Watch and Download buttons, and the shared notes panel. Follow/unfollow also
work from this tab.

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

Games the original client recorded have no `gameVersionKind`. For those, Watch
tries the ordinary Release/Prerelease checkouts first (their histories share
most of the original commits, so no extra download is usually needed), then the
**Original** checkout: a dedicated `<RootPath>/Original` clone of the original
repository, with the Release, Prerelease, old-release, and nightly sources as
additional remotes — the same set of sources Fraud Launcher 2 can backdate to.
The maintainer's own games are still recognised (and labelled Plus) when the
commit is reachable from a fork remote. A version that no published source has
fails with a clear "no longer published" message.

A running game blocks Watch with a clear message. Play/Update on the Play page
puts the tracked branch back afterwards. The first launch after a build switch
rebuilds `temp.mpq` (~1–2 minutes, black screen).
