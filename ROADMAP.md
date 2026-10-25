# Roadmap

This document tracks where the Cosmonarchy Plus Launcher is heading. The top
item is the one currently planned; "shipped" entries are already in the app.

## Planned

### 1. Self-hosted account & statistics backend

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
- `/api/certifications/{matchUuid}` is public: it is how one launcher verifies
  another player's claim.

**Client work when the server exists**

- Implement `ServerAccountBackend : IAccountBackend` with the same methods the
  local backend has; the Statistics tab needs no changes.
- First launch prompts to "link" an existing local account (upload local
  certifications) or sign in fresh.
- The Replays tab shows a match as certified when *any* verified account
  certifies it (server truth) instead of only the local account.

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
