# cncnet-host-bot

Always-on bot that hosts CnCNet Yuri's Revenge game rooms without running the game. It speaks
the CnCNet client's IRC lobby protocol directly (see `docs/protocol/`), so players use their
normal client and the bot is only ever the room host, never a player in the match.

What it does:

- Hosts one room per configured bot nick (each room has its own IRC connection).
- Admits or kicks joiners by how long their ident has been seen in the public CnCNet chat logs
  (`https://files.cncnet.org/logs/yr/ChatLogs/`): a "newbie" room kicks idents older than N days,
  a "pro" room kicks idents younger than N days or never seen.
- Picks a random map from the pool after every game and after 20 minutes with an empty room.
- Relays player options, ready states and V3 dynamic-tunnel negotiation reports exactly like a
  client host, and launches the game when everyone is ready.
- Logs every chat line, join, kick and match to SQLite.
- Writes its own log to `logs/hostbot-yyyy-MM-dd.log` (one file per UTC day, old files pruned after
  `logging.keepDays`); `-v` or `logging.debug` adds every IRC line.
- Serves an admin page: rooms (create, edit, stop, delete, kick, ban, next map, chat), chat
  log, match history, ident search with per-room verdicts, events, the live lobby game list,
  whitelist and bot log.

## Layout

```
src/HostBot/            .NET 10 console app (bot + admin web server)
  Irc/                  IRC client (plain TCP, CnCNet registration, priority send queue)
  Lobby/                GameRoom (host state machine), RoomHost (connection lifecycle), RoomManager
  Idents/               chat-log importer, SQLite ident store, kick policy
  Maps/                 MPMaps.ini loader, map pool, game-option layout
  Tunnels/              master list, tunnel choice, V2 port request
  Storage/              bot database (rooms, chat, events, matches)
  Web/                  admin API; wwwroot/admin.html is the page
gamedata/maps.json      map pool built from a game install (hostbot maps build)
gamedata/options.json   game-option control layout of the players' client package
docs/protocol/          wire-protocol reference extracted from the client source
config.json             sample configuration
```

## Setup

Requirements: .NET 10 SDK to build; the published binary is self-contained.

1. Build the map pool from a game install that has the same `INI/MPMaps.ini` and `Maps/` as
   the players (the SHA1 of every map file must match theirs):

   ```
   dotnet run --project src/HostBot -- maps build "C:\path\to\game" gamedata/maps.json
   ```

2. Download the chat logs into the ident database (one file per day since 2022; about 1.4 GB
   downloaded once, the database is ~1 GB). Re-running only fetches missing days plus the last
   three, so run it again any time:

   ```
   dotnet run --project src/HostBot -- idents import config.json
   dotnet run --project src/HostBot -- idents check yr.abc123 config.json
   ```

3. Copy `config.json` to `config.local.json` (git-ignored) and edit: room names, bot nicks,
   tiers and day limits, the admin password. Rooms in the config are only seeds: on the first
   start they are copied into the database and from then on the admin page owns them.

4. Run:

   ```
   dotnet run --project src/HostBot -- run config.local.json
   ```

   and open the admin page (default `http://127.0.0.1:8585`, any user name, the configured
   password). Keep it on localhost and put nginx with TLS in front for remote access.

## Checking the protocol against the live client

The bot learns the client version, protocol revision letter and GAME field count from the
other hosts in `#cncnet-yr-games` at start-up: the revision and field count most hosts use, and
within those the newest version at least three hosts broadcast, so a room keeps up with a client
update while most of the lobby is still on the old build (override with `irc.clientVersion`,
`irc.protocolRevision`, `irc.gameBroadcastFields`). The game-option layout in
`gamedata/options.json` cannot be learned; verify it after a client update with:

```
hostbot capture                 # prints every GAME broadcast: revision, version, field count
hostbot capture <host nick>     # joins that host's room as a silent player and prints its GO/PO/PEO
```

A GO from a real host must have the same number of `;` fields as the bot would send
(`checkBoxInts + dropDowns + 10`, plus 1 when the GAME broadcast has 15 fields).

## Deploying on Linux

```
dotnet publish src/HostBot -c Release -r linux-x64 --self-contained -o publish/linux-x64
rsync -a publish/linux-x64/ gamedata/ config.local.json user@server:/opt/hostbot/
```

The bot needs outbound TCP 6667 (GameSurge), HTTPS to cncnet.org/files.cncnet.org, HTTP to V2
tunnels (only in V2 mode) and ICMP (optional, for tunnel ranking). Example systemd unit:
`deploy/hostbot.service`.

## Room settings

| setting | meaning |
|---|---|
| `tier` | `Noob` (kick old idents), `Pro` (kick young/unknown idents), `Open` |
| `maxIdentAgeDays` / `minIdentAgeDays` | the day limits for the two tiers |
| `useHostAsFallback` | newbie rooms also look at the cloaked host's first-seen date, so a fresh ident on a known connection still counts as old |
| `voteKickBanHours` | `!votekick <nick>`: half of the other players (at least two) remove a player and keep them out of this room for this many hours (in memory, so a bot restart clears it). Admin kicks with ban get the same 24 h. 0 disables the command |
| `minActiveDays` | any tier: refuse idents seen in the chat logs on fewer than this many days; a handed-over ident's days count, so a regular on a new PC still passes. Counts play, not ident age. 0 = off |
| `nickHistoryMinDays` | newbie rooms also refuse a nick handed over from an older ident (a veteran on a new PC): another ident played the nick on at least this many days, was first seen more than `maxIdentAgeDays` ago, and went quiet when the joining ident appeared (no later than 3 days after it, no more than 180 days before). Nicks are not registered, and a shared name's other users keep playing on their own idents, so they do not match. 0 = ident only |
| `gameMode`, `maxPlayers`, `requireTeamStartMappings` | map pool filters: maps of the game mode for the room size (4 = 2v2, 6 = 3v3) that have a two-team start layout |
| `blitzGameMode` | mode that `!blitz` switches the room to by vote (default `Blitz 2v2`), playing that mode's maps that pass the other filters; the room returns to `gameMode` once it is empty. Empty disables the command |
| `blitzGameOptions` | option overrides applied on top of the Blitz mode's forced options while `!blitz` is in effect (default: superweapons on) |
| `maps` / `excludedMaps` | maps ticked on the admin page although they are outside the filters / maps ticked off (MPMaps.ini section names) |
| `autoThreeVsThree`, `autoThreeVsThreeLobbyUsers` | enlarge the room to 3v3 while the lobby channel has more than N users; switches only while the room is empty and drops back 10% below N |
| `nextMapPenalty` | each passed `!next` lowers that map's pick weight in this room: 1 / (1 + penalty × max(0, skips − plays/2)), never below 0.2 |
| `minPlayersToStart` | launch as soon as this many are present and all are ready (equal to `maxPlayers` = a full room, also when enlarged to 3v3) |
| `maxSpectators` | extra seats for people who pick Spectator in the side list. The room goes invite-only as soon as the player slots are taken, so spectators can only join while a player slot is still free; anyone who slips in as the slots fill has 30 s to pick Spectator, and a spectator can take a player slot again only while one is free |
| `idleMapRotateMinutes` | change map after this long with nobody in the room |
| `tunnel.mode` | `V3Dynamic` (per-pair best tunnel, recommended), `V3` (one host-picked tunnel), `V2` |
| `tunnel.preferred` | name/country fragments tried in order for the static modes |
| `gameOptions` | check box / drop-down overrides by control name, e.g. `chkCrates=true`, `cmbCredits=3` |

In-room chat commands for players: `!map`, `!next` (vote for a random other map), `!mappick <part of a name>` (half the room voting for the same map switches to it), `!vote <option> [option ...]` (each option is its own vote; `!vote sw france` casts two), `!blitz` (vote to switch to the Blitz 2v2 mode and its maps, and back), `!votekick <nick>` (half of the other players remove a player for a day), `!rules`, `!help`, `!invite <nick>` where enabled.
