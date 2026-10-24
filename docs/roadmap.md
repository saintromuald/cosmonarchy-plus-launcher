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

## M2 — Windows parity

- [ ] Windows launch path verified (exe, window-title wait)
- [ ] Defender exclusions + firewall rules (PowerShell) when needed
- [ ] VC++ redist download for clean Windows installs

## M3 — Distribution

- [x] GitHub Actions: build/test, win-x64 single-file exe, linux-x64, AppImage
- [x] Arch package (PKGBUILD) for Omarchy
- [x] AppImage recipe
- [ ] Flatpak (stretch)
- [x] Self-updater from GitHub Releases

## M4 — Replace the scripts

- [ ] Desktop entries point at the new launcher
- [ ] `omarchy-launch-cosmonarchy*`, `cosmo-variant` retired on both machines
- [ ] Omarchy menu category "Cosmonarchy"
