# Roadmap

## M0 — Core on Linux (in progress)

- [x] Repo catalog (my builds first, upstream fallback, unofficial mirrors)
- [x] RepoManager (clone/fetch/reset/log/unshallow)
- [x] Install pipeline (StarCraft, patches.yml, CrownLink, maps, Wine ddraw)
- [x] Variant staging + launch (Linux umu; Windows path written, untested)
- [x] Account API client
- [x] Settings persistence
- [x] Smoke tests: live prefix + throwaway full pipeline (`tools/Smoke`)
- [x] Port `cosmo-tempmpq-ensure` (truncated temp.mpq healing, verified on the real archive)
- [ ] First real install test against the existing `~/Games/cosmonarchy` prefix

## M1 — UI

- [x] Play page: repo picker, version log, install/play buttons, progress
- [x] Multiple installed versions: install any number side by side (each in
  its own checkout), pin or track, switch with one click, update, delete
- [x] Settings page: root path, variant, role, action-on-play, log depth, updates
- [x] Account page: login flow, profile stats, community link
- [x] Replays page: list + open folder (deep-link launch in M4)
- [x] Favourites tab: favourites with add dates and match snapshots,
  watched/new split, notes, saved lists with names + titles
- [x] Desync highlighting: `hasDesyncFile` games shown red with a ⚠ badge
- [x] Sharing: Discord-ready messages (note + import code block), share files,
  optional Discord webhook posting, import from paste/clipboard/file
- [x] Community tab: public share board on a GitHub repo, publish/unpublish
  with Discord-name stamping from the account login

## M2 — Windows parity

- [ ] Windows launch path verified (exe, window-title wait)
- [ ] Defender exclusions + firewall rules (PowerShell) when needed
- [ ] VC++ redist download for clean Windows installs

## M3 — Distribution

- [x] GitHub Actions: build/test, win-x64 single-file exe, linux-x64, AppImage
- [x] Arch package (PKGBUILD) for Omarchy
- [x] AppImage recipe
- [ ] Flatpak (stretch)
- [x] Self-updater: in-app check + one-click install and restart (directory
  installs on Linux, single-file exe on Windows; AppImage/package installs
  fall back to the release page)

## M4 — Replace the scripts

- [ ] Desktop entries point at the new launcher
- [ ] `omarchy-launch-cosmonarchy*`, `cosmo-variant` retired on both machines
- [ ] Omarchy menu category "Cosmonarchy"
