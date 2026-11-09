# cncnet-host-bot

Hosts CnCNet game rooms around the clock without running the game. It talks the XNA client's
IRC lobby protocol (`docs/protocol/`), so players join with their normal client and the bot only
ever hosts.

Built for Yuri's Revenge. Other games on the same client (RA1, TS) work with their own map
pool and option layout; see [Other games](#other-games).

- One room per bot nick, each on its own IRC connection.
- Ident checks per room: beginner rooms refuse long-known players, pro rooms refuse new ones.
  Ident history comes from the public CnCNet chat logs.
- Random map after every game, map and option votes, spectators, `!blitz`, `!votekick`.
- Starts the game when everyone is ready and every tunnel pair is connected.
- Admin page for rooms, chat, matches, idents, whitelist and the bot log.
- Everything is logged to SQLite; the bot log goes to `logs/hostbot-yyyy-MM-dd.log` (UTC).

## Layout

```
src/HostBot/
  Irc/        IRC client with a flood-aware priority queue
  Lobby/      GameRoom (the host), RoomHost (connection), RoomManager
  Idents/     chat-log import, ident store, join policy
  Maps/       map pool and option layout builders
  Tunnels/    tunnel list and selection
  Storage/    bot database
  Web/        admin API; wwwroot/admin.html
gamedata/     maps.json and options.json for YR
docs/protocol/  lobby protocol notes taken from the client source
config.json   sample config
```

## Setup

Needs the .NET 10 SDK to build. The published binary is self-contained.

```
# map pool and option layout from a game install matching the players' (map SHA1s must match)
hostbot maps build    "C:\path\to\game" gamedata/maps.json
hostbot options build "C:\path\to\game" gamedata/options.json

# ident history (YR only, ~1 GB; re-run any time, it only fetches what is missing)
hostbot idents import config.json
hostbot idents check yr.abc123 config.json

hostbot run config.local.json
```

`hostbot` is `dotnet run --project src/HostBot --` when working from source.

Copy `config.json` to `config.local.json` (git-ignored) and set the nicks, rooms and admin
password. Rooms in the config only seed the database on first start; after that the admin page
owns them. The admin page listens on `127.0.0.1:8585` by default; put nginx with TLS in front.

GameSurge allows 4 connections per IP. `irc.maxConnections` caps the bot's own; lower it if
something else on the same IP is connected too.

## Other games

One bot process hosts one game. For RA1 or TS run a second process with its own config:

- `irc.game`: `RA` or `TS` (channel names follow it).
- `mapPool` / `optionLayout`: build both from that game's client package.
- `chatLogs.baseUrl`: empty. Only YR has public chat logs, so there is no ident history: use
  `Open` rooms with `minActiveDays` 0.
- `requireTeamStartMappings` / `useTeamStartMappings`: false if the maps have no team layouts
  (RA1 has none). Players then pick teams and the bot waits for two even teams.
- `voteOptions`: list the game's options. Without it the YR set is used and anything the
  option layout does not have is dropped.
- `blitzGameMode` is ignored when the game has no such mode.

Check the result against a real host before going live:

```
hostbot capture                       # GAME broadcasts: revision, version, field count
hostbot capture <host nick> config    # joins that room silently, prints its GO/PO/PEO
```

A real host's GO must have as many `;` fields as the bot's.

## Client updates

The bot learns the client version from other hosts at start-up (the newest version that at
least three hosts use), so after an update a restart is enough. If the update changed the lobby
options or maps, rebuild `options.json` and `maps.json` first.

## Deploying

```
dotnet publish src/HostBot -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64
```

Copy `publish/linux-x64/`, `gamedata/` and `config.local.json` to the server. Needs outbound
TCP 6667, HTTPS to cncnet.org and files.cncnet.org, and ICMP for tunnel ranking (optional).
Example unit and nginx config in `deploy/`.

## Room settings

| setting | |
|---|---|
| `tier` | `Noob` refuses old idents, `Pro` refuses young or unknown ones, `Open` admits anyone |
| `maxIdentAgeDays` / `minIdentAgeDays` | day limits for `Noob` / `Pro` |
| `useHostAsFallback` | `Noob`: a new ident on a long-known host counts as old |
| `nickHistoryMinDays` | `Noob`: refuse a nick handed over from an older ident that played it this many days. 0 = off |
| `minActiveDays` | any tier: refuse idents seen on fewer days. 0 = off |
| `allowInvites`, `inviteDays`, `maxInvitesPerDay` | `!invite <nick>` lets a player vouch for someone the check would refuse |
| `gameMode`, `maxPlayers` | map pool: maps of this mode for this room size (4 = 2v2, 6 = 3v3) |
| `requireTeamStartMappings`, `useTeamStartMappings` | only maps with a two-team layout / auto-ally from that layout |
| `forceRandomStarts` | spots locked to Random (and so random teams with auto-ally). `!vote spots` flips it, unlisted |
| `maps` / `excludedMaps` | extra maps / removed maps (MPMaps.ini section names), set from the admin page |
| `minPlayersToStart` | start once this many are ready |
| `maxSpectators` | extra seats for Spectator. The room locks once the player slots are full |
| `readyGraceSeconds`, `kickUnreadySeconds` | in a full room: warn a player who is not ready, then kick |
| `startDelaySeconds` | countdown once everyone is ready |
| `votesNeeded` | votes needed for `!vote`, `!next` and `!blitz`. 0 = `!vote` off |
| `voteKickBanHours` | `!votekick`: half of the other players remove someone for this long. 0 = off |
| `blitzGameMode`, `blitzGameOptions` | mode and option overrides for `!blitz`; empty mode = off |
| `autoThreeVsThree`, `autoThreeVsThreeLobbyUsers` | become 3v3 while the lobby has more than N users |
| `nextMapPenalty` | how much a passed `!next` lowers that map's chance in this room |
| `idleMapRotateMinutes` | new map after this long empty |
| `tunnel.mode` | `V3Dynamic` (recommended), `V3`, `V2` |
| `gameOptions` | option overrides by control name, e.g. `chkCrates: "true"`, `cmbCredits: "3"` |

Player commands: `!help`, `!map`, `!next`, `!mappick <name>`, `!vote <option> [...]`, `!blitz`,
`!votekick <nick>`, `!rules`, `!invite <nick>`.
