# Roadmap

This document tracks where the Cosmonarchy Plus Launcher is heading. The top
item is the one currently planned; "shipped" entries are already in the app.

## Planned

### 1. Plus replays on our own server (target: 2 Nov 2026)

Plus games currently auto-upload to the official Replay Kraken at
cosmonarchy.com, polluting their match list and per-player stats and confusing
players there (Plus replays do not replay on vanilla Cosmonarchy). The fix
moves Plus uploads to the self-hosted fork (`saintromuald/replay-kraken`) and
keeps the official site 100% clean.

**Code status** (written, uncommitted across the three repos):

- gptp client (`cosmonarchy-gptp`): the upload endpoint reads
  `replay-kraken-server` / `replay-kraken-token` / `replay-kraken-upload-all`
  from `cosmonarchy.yml` (empty server = uploads disabled, so no build ever
  leaks games upstream), and sends the `meta` payload — map, gameType,
  durationFrames, matchup, and players with race/result/human-or-computer.
- Server (`replay-kraken`): accepts `meta`, stores player type (AI games stay
  out of vs-race stats), `/api/users/@me` compatibility endpoint, RFC 3339
  timestamps.
- Launcher: Settings → "Plus replay server"; the Replays, Statistics and
  Favourites tabs merge that server's list into the Plus channel; replay
  downloads route per match.

**Left to do**

- [ ] Host the server (VPS + Docker, HTTPS, `RK_TOKENS`, Discord webhook)
- [ ] Build and ship the gptp client with the config keys (cpp-httplib ssl)
- [ ] Set `replay-kraken-server` for Plus installs (installer or launcher
      post-install step)
- [ ] Commit and release the three repos
- [ ] Pick a distinct name for the self-hosted service so players never
      confuse it with Replay Kraken itself
- [ ] Agree with the No-Frauds Club how pre-split Plus games already on the
      official list are handled

### 2. Self-hosted account & statistics backend

Today statistics accounts live on the local machine: email + password, a
claimed in-game name, and per-account match certifications, all stored in the
launcher's data directory (see `Statistics` tab). The obvious next step is
moving accounts to a server so:

- stats and certifications follow you across machines,
- a certified match is certified *for everyone* (any launcher can verify that
  "Groovy" really played it), and
- the top-10 opponents / matchup views become a public profile page.

**What needs to be built**

- A small service (any stack; a Rust/Axum or Node/Fastify app with SQLite or
  Postgres is plenty) hosting the contract below.
- Email verification is out of scope for v1; password reset can be a later
  follow-up.

**API contract the server must implement** (the client is already prepared —
  see `CosmonarchyPlusLauncher.Core/Services/IAccountBackend.cs`)

| Method | Path | Body | Returns |
|---|---|---|---|
| POST | `/api/accounts/register` | `{ email, password, playerName }` | `{ token, account }` |
| POST | `/api/accounts/login` | `{ email, password }` | `{ token, account }` |
| GET | `/api/accounts/@me` | — (Bearer) | `{ id, email, playerName, createdAt }` |
| PATCH | `/api/accounts/@me` | `{ playerName }` (Bearer) | `{ account }` |
| PUT | `/api/accounts/@me/certifications/{matchUuid}` | — (Bearer) | `{ certified: true }` |
| DELETE | `/api/accounts/@me/certifications/{matchUuid}` | — (Bearer) | `{ certified: false }` |
| GET | `/api/stats/{playerName}` | — | aggregated profile: games, races, matchups, top opponents |
| GET | `/api/certifications/{matchUuid}` | — | which accounts certify the match |

Rules:

- Passwords hashed server-side (Argon2id or scrypt), never stored plaintext.
- Player names are claims: registration does **not** reserve a name, because
  in-game names are not unique today. Certification is what gives a name
  authority — an account's certified matches are the identity, not the name.
- Every certification carries the certifier's **email and claimed name**
  (the client already stores and displays this). The email is the
  spoof-resistant part: anyone can copy an in-game name, but nobody can fake
  an email that the real player publicly uses.
- `/api/certifications/{matchUuid}` is public: it is how one launcher shows
  *who* certified a match (name + email per certification, with timestamps).

**Client work when the server exists**

- Implement `ServerAccountBackend : IAccountBackend` with the same methods the
  local backend has; the Statistics tab needs no changes.
- First launch prompts to "link" an existing local account (upload local
  certifications) or sign in fresh.
- The Replays tab shows a match as certified when *any* verified account
  certifies it (server truth) instead of only the local account, and shows
  each certifier as "name (email)".

**Deployment note**

The community backend at cosmonarchy.com is run by the No-Frauds Club, not by
this project. The self-hosted backend should be a separate service that we
control; the launcher's `Account` tab (Discord OAuth) is unaffected.

## Shipped

- Statistics tab with local email/password accounts: races played, race-vs-race
  matchups, games/wins/losses, and top-10 most-played opponents, computed from
  the ReplayKraken match list.
- Match certification: certify games as "really me" from the Statistics tab or
  the Replays tab's note panel; uncertified games are tagged and searchable
  (`certified` / `uncertified` in the Replays search box).
