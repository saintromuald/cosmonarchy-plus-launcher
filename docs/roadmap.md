# Roadmap

## M0 — Core on Linux (in progress)

- [x] Repo catalog (my builds first, upstream fallback, unofficial mirrors)
- [x] RepoManager (clone/fetch/reset/log/unshallow)
- [x] Install pipeline (StarCraft, patches.yml, CrownLink, maps, Wine ddraw)
- [x] Variant staging + launch (Linux umu; Windows path written, untested)
- [x] Account API client
- [x] Settings persistence
- [ ] Port `cosmo-tempmpq-ensure` (truncated temp.mpq healing)
- [ ] First real install test against the existing `~/Games/cosmonarchy` prefix

## M1 — UI

- [ ] Play page: repo picker, version log, install/play buttons, progress
- [ ] Settings page: root path, variant, role, action-on-play, log depth
- [ ] Account page: login flow, profile stats, social links
- [ ] Replays page: list + launch

## M2 — Windows parity

- [ ] Windows launch path verified (exe, window-title wait)
- [ ] Defender exclusions + firewall rules (PowerShell) when needed
- [ ] VC++ redist download for clean Windows installs

## M3 — Distribution

- [ ] GitHub Actions: win-x64 single-file exe
- [ ] Arch package (PKGBUILD) for Omarchy
- [ ] AppImage
- [ ] Flatpak (stretch)
- [ ] Self-updater from GitHub Releases

## M4 — Replace the scripts

- [ ] Desktop entries point at the new launcher
- [ ] `omarchy-launch-cosmonarchy*`, `cosmo-variant` retired on both machines
- [ ] Omarchy menu category "Cosmonarchy"
