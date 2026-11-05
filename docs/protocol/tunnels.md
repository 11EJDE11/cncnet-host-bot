# CnCNet tunnels: what a non-playing host bot must do

Source of truth: `C:\Users\QWE\source\repos\11EJDE11\xna-cncnet-client` (branch `develop`,
HEAD `64cfd548`). All `file:line` cites below are relative to that repo root unless marked
otherwise. Server-side cites are from `C:\Users\QWE\source\repos\cncnet-server` (commit `3c2a5e2`,
2022-12-09, which predates the matchmaking/STUN features; see "Provenance").

Everything here is about wire protocol and host bookkeeping. UI is omitted on purpose.

## Provenance: V3 dynamic mode is upstream CnCNet, not fork-only

- `cd551558 Add support for V3 tunnels (#845)` (2026-08-25, author 11EJDE11) and the follow-ups
  `90eebc7a` (#1066 ephemeral local port), `38982f14` (remove ICMP-based failure detection),
  `308f247f` (remove TNLRENEG), `228cd7d4` (quiet ping broadcasts) are **all ancestors of
  `upstream/develop`** (`git merge-base --is-ancestor cd551558 upstream/develop` = yes; upstream =
  `https://github.com/CnCNet/xna-cncnet-client.git`, tip `df93185c` 2026-11-05).
- `git log upstream/develop..HEAD -- DXMainClient/Domain/Multiplayer/CnCNet CnCNetGameLobby.cs` is
  **empty**: this fork carries no tunnel-related changes beyond upstream.
- Upstream release tags `2.14.0-rc.1` .. `2.14.0-rc.8` all contain `cd551558`.
- The sibling clone `C:\Users\QWE\source\repos\11EJDE11\xna-cncnet-client-develop` (detached HEAD
  `54ff7cb7`) contains the identical set of files (`V3TunnelNegotiationManager.cs`,
  `TunnelMode.cs`, `TunnelNegotiationCommands.cs`, ... all present) with the same history.

Therefore every stock 2.14 client in the wild speaks the protocol below; the bot can assume it.

## 0. Tunnel modes (`TunnelMode.cs:6-11`)

| enum | int on the wire (GO field) | meaning |
|---|---|---|
| `V3Static` | 0 | all players use the one host-selected V3 tunnel |
| `V3Dynamic` | 1 | every player pair negotiates its own V3 relay (or P2P) path |
| `V2Legacy` | 2 | host-selected V2 tunnel, HTTP port request |

- Mode is derived on join from the tunnel object: `null` -> `V3Dynamic`, `Version == 2` ->
  `V2Legacy`, else `V3Static` (`TunnelModeExtensions.FromTunnel`, `TunnelMode.cs:19-21`;
  called from `CnCNetGameLobby.SetUp`, `CnCNetGameLobby.cs:323`).
- User default is `TunnelMode=1` (V3Dynamic) (`ClientCore/Settings/UserINISettings.cs:178`).
- The GAME broadcast's tunnel field is `[DYN]` for dynamic, otherwise `addr:port`
  (`CnCNetGameLobby.cs:3137-3142`). Joining clients parse it in `CnCNetLobby.cs:1616-1639`:
  `[DYN]` -> tunnel `null`; otherwise the tunnel **must** be found in the client's own master-list
  copy by exact `Address` and `Port`, else the game is **not listed at all**
  (`CnCNetLobby.cs:1641-1644`).

## 1. Master list

### URL

- `NetworkDefinitions.ini [Settings] CnCNetTunnelListURL`, default
  `https://cncnet.org/master-list` (`ClientCore/ClientConfiguration.cs:542`). The YR package
  sets the same value (`cncnet-yr-client-package/package/Resources/NetworkDefinitions.ini:2`).
- Fetched with a 10 s timeout, 2 attempts (`TunnelHandler.cs:298-331`), cached to
  `<user files>/tunnel_cache` (`TunnelHandler.cs:362-417`).
- Refresh cadence: every `CurrentTunnelPingIntervalSeconds` (default 20 s) the client either pings
  the current tunnel or, every `CyclesPerTunnelListRefresh`-th cycle (default 3, i.e. every 60 s),
  re-downloads the list (`TunnelHandler.cs:423-444`, defaults `ClientConfiguration.cs:702,707`).

### Response format (verified live 2026-11-06)

Plain text, one entry per line, first line is a header and is skipped (`TunnelHandler.cs:372-373`):

```
address;country;countrycode;name;password;clients;maxclients;official;latitude;longitude;version;distance
51.75.126.186:50001;France;FR;[EU]Hottwire.me;0;10;100;2;0;0;3;0
51.75.126.186:50000;France;FR;[EU]Hottwire.me;0;2;100;2;0;0;2;0
51.75.126.186:50002;France;FR;[EU]Hottwire.me (Matchmaking);0;0;250;2;0;0;4;0
```

Parsing (`CnCNetTunnel.Parse`, `CnCNetTunnel.cs:46-90`), fields split on `;`:

| idx | field | type / meaning |
|---|---|---|
| 0 | `address` | `ip:port`, split on `:` -> `Address` (string, IPv4 literal), `Port` (int) |
| 1 | `country` | string |
| 2 | `countrycode` | string (ISO-2) |
| 3 | `name` | string (server strips `;` from it, server `TunnelV3.cs:51`) |
| 4 | `password` | `RequiresPassword = parts[4] != "0"`; password tunnels are dropped (`TunnelHandler.cs:382`) |
| 5 | `clients` | int, current registered clients |
| 6 | `maxclients` | int |
| 7 | `official` | int status: `2` -> `Official=true`; `1` -> `Recommended=true`; else neither |
| 8 | `latitude` | double, invariant culture |
| 9 | `longitude` | double |
| 10 | `version` | int: `2` = V2 (HTTP+UDP), `3` = V3 relay, `4` = matchmaking server (speaks V3 on the wire, carries no games, `CnCNetTunnel.cs:198-212`); anything else is dropped (`TunnelHandler.cs:42,385`) |
| 11 | `distance` | double (server-computed from the requester's geo-IP; `0` in the live sample) |

Live counts on 2026-11-06: 80 v2, 72 v3, 7 v4, 1 v5 (dropped). Duplicate `addr:port` entries are
dropped (`TunnelHandler.cs:388`). Note: the same server usually appears once per version on
different ports (v2 at `:50000`, v3 at `:50001`, matchmaking at `:50002`).

### How the client pings tunnels

- **ICMP only.** `CnCNetTunnel.MeasurePing` uses `System.Net.NetworkInformation.Ping` with a
  1000 ms timeout against `Address` (`CnCNetTunnel.cs:288-305`). One probe per unique IP; the
  result is copied to every tunnel entry sharing that IP (`TunnelHandler.cs:249-267`).
- Only official/recommended tunnels are probed unless the user enables
  `PingUnofficialCnCNetTunnels` (`TunnelHandler.cs:213-217`). Matchmaking (v4) servers are never
  probed.
- A lost probe keeps the last good value for `RetainedPingFailures` (default 2) refreshes
  (`CnCNetTunnel.cs:137-151`, `ClientConfiguration.cs:720`).
- There is **no V2/V3 UDP ping packet in the current client.** (The 2022 server still answers a
  legacy 50-byte UDP datagram with sender=receiver=0 by echoing 12 bytes, server `TunnelV3.cs:179-186`
  and `TunnelV2.cs:277-285`, but no client code sends it.) The only client->tunnel UDP traffic
  besides game data is the V3 `Register` packet (section 3) and STUN (section 4.6).

### Default selection

- **Host UI default (TunnelListBox, `TunnelListBox.cs:129-233`)**: among tunnels of the target
  version that are `Official || Recommended` and have a valid ping, pick the lowest
  `rating = round(ping_ms^2 * usageRatio*100)` where `usageRatio = clients/maxclients`
  (`0` is treated as `0.1`).
- **Auto-select after a mode switch to static/V2 (`AutoSelectBestTunnel`,
  `CnCNetGameLobby.cs:2706-2724`)**: lowest raw `Ping.Milliseconds` among tunnels of the target
  version with a valid ping (official/recommended unless unofficial pinging is on). It then
  broadcasts `CHTNL addr:port`.
- A bot without ICMP can rank by `distance` (field 11) or `latitude/longitude`; the dynamic-mode
  fallback ranking in the client does exactly that for unmeasured tunnels
  (`TunnelShortlist.cs:413-421`).

## 2. V2 tunnels (`TunnelMode.V2Legacy`)

### Host-side HTTP port request

`CnCNetTunnel.GetPlayerPortInfo(int playerCount)` (`CnCNetTunnel.cs:250-282`):

- `GET http://{Address}:{Port}/request?clients={playerCount}` (plain HTTP, 10 s timeout).
- Response body: `[id1,id2,...,idN]`. The client strips `[`/`]`, splits on `,`, `Convert.ToInt32`.
- Server side (`cncnet-server TunnelV2.cs:179-238`): `clients` must be 2..8 else HTTP 400; ids are
  random **signed 16-bit** values (`rand.Next(short.MinValue, short.MaxValue)`) not currently
  mapped; HTTP 503 if fewer than 2 could be allocated or in maintenance. Each allocated id is a
  mapping that expires after 30 s without traffic (`TunnelClient.cs:16-22`) -- so request ports
  immediately before sending STARTV2.
- `HostLaunchGame` calls it with `Players.Count` (`CnCNetGameLobby.cs:1186`); if
  `playerPorts.Count < Players.Count` the launch is aborted (`:1188-1195`).

### Port-to-player mapping and STARTV2

`SendStartV2ToPlayers` (`CnCNetGameLobby.cs:1235-1251`): `Players[i].Port = playerPorts[i]`
in player-list order, then one CTCP:

```
STARTV2 {UniqueGameID};{name1};{tunnelAddr}:{port1};{name2};{tunnelAddr}:{port2};...
```

- `UniqueGameID` is an `int` built from `i + DD MM HH mm` digits (`MultiplayerGameLobby.cs:311-329`);
  any non-negative int the clients can parse is accepted (`CnCNetGameLobby.cs:2247-2249`).
- `tunnelAddr` is the **IP only** (`CurrentTunnel.Address`), no port. It is repeated for every
  player.
- Receiver (`NonHostLaunchGameV2`, `:2231-2305`): accepts only from `hostName`; for each
  `(name, ip:port)` pair sets `pInfo.Port = port`; unknown names abort the launch silently
  (`:2294-2297`). For the receiver's **own** entry, if the ip does not equal its
  `CurrentTunnel.Address` it re-selects the tunnel from its list with `Version == 2 &&
  Address == ip` (`:2273-2292`); if none matches it shows an error and does not start.
  The port in the message is never checked against the current tunnel's port.

### What each player's game connects to (`WriteSpawnIniAdditions`, `CnCNetGameLobby.cs:2413-2436`)

```ini
[Tunnel]
Ip=<CurrentTunnel.Address>
Port=<CurrentTunnel.Port>        ; the V2 UDP port of the tunnel (e.g. 50000)
[Settings]
GameID=<UniqueGameID>
Host=<IsHost>
Port=<own id from STARTV2>
[Other1..N]                      ; GameLobbyBase.cs:1819-1826
Name=<peer name>  Ip=0.0.0.0  Port=<peer id from STARTV2>
```

The game/spawner itself speaks the V2 UDP framing to the tunnel (2-byte sender id BE, 2-byte
receiver id BE, payload; server `TunnelV2.cs:265-266`). The client process is not in the data path.

## 3. V3 static tunnels (`TunnelMode.V3Static`)

### Player ids

`V3TunnelNegotiationManager.GeneratePlayerID(name)` (`V3TunnelNegotiationManager.cs:71-76`):

```
id = ReadUInt32LittleEndian( SHA1( UTF8( name + ":" + channelName ) ) [0..4) )
```

- `uint`, full 32-bit range, **deterministic**: every client computes the same id for every
  player without communication. `channelName` is the IRC game channel name (`IV3NegotiationHost
  .ChannelName`, `CnCNetGameLobby.cs:1276`). Collisions are possible but only break the decider
  tie-break, which then falls back to ordinal name order (`V3PlayerNegotiator.cs:177-182`).
- The in-game id (`PlayerGameId`, the 16-bit id the game writes in its UDP packets) is
  `48000 - position`, position = index in the STARTV3 message (`V3TunnelNegotiationManager.cs:767,804`).

### STARTV3 (`GenerateV3StartPayload`, `V3TunnelNegotiationManager.cs:796-835`; sent at `CnCNetGameLobby.cs:1256`)

```
STARTV3 {UniqueGameID};{id1};{name1};{addr1}:{port1};{id2};{name2};{addr2}:{port2};...
```

- One triple per entry of `host.Players`, **in host player-list order**; order defines each
  player's game port (`48000 - i`) and must match the receivers' `Players` order (which they got
  from the host's `PO` message).
- `id` = decimal `uint` from `GeneratePlayerID(name)`.
- `addr:port` = in static mode the host's `CurrentTunnel.Address:Port` for every player
  (`:815-822`); in dynamic mode `0.0.0.0:0` (or the player's negotiated tunnel if the host knows
  it; the receivers ignore it, see `:818-819`).
- Receiver `NonHostLaunchGameV3` (`CnCNetGameLobby.cs:2310-2362`): accepts only from `hostName`;
  requires `parts.Length == Players.Count*3 + 1` exactly, else logs and shows "Failed to process
  the game start message" (`:2330-2335`). Per entry `ApplyV3StartEntry`
  (`V3TunnelNegotiationManager.cs:752-788`): id must parse as uint, name must exist in `Players`,
  and **in non-dynamic mode the `addr:port` must resolve to a tunnel in the receiver's own master
  list** (`:777-782`) or the start fails. So in V3 static the bot must put the exact
  `Address:Port` of a listed v3 tunnel.
- Note the saved-game loading lobby uses the same command name with **no game-id prefix**
  (`parts.Length == Players.Count*3`, `CnCNetGameLoadingLobby.cs:623-640`). Not relevant to a
  fresh-game bot but do not reuse that code path.

### Does the host talk to the V3 tunnel? No.

- `HostLaunchGame` for `CurrentTunnel.Version == 3` does nothing but reserve its own local UDP
  port and send STARTV3 (`CnCNetGameLobby.cs:1199-1216`). There is no HTTP call and no explicit
  per-game registration by the host on behalf of others.
- Each **playing** client registers itself implicitly: the server creates/refreshes the
  `senderId -> endpoint` mapping on **any** datagram whose first 4 bytes (sender id) are non-zero
  (server `TunnelV3.cs:189-213`), and forwards only if the receiver id is already mapped
  (`:215-217`). Mappings expire after 30 s idle (`TunnelClient.cs:16`). The client's bridge never
  sends an explicit `Register` in static mode; the first game datagram does it.
  UNVERIFIED for the current production server (the client comments at
  `V3TunnelCommunicator.cs:325-326` and `CnCNetTunnel.cs:218-219` say "a server only forwards to
  registered clients" and "a full server simply drops the registration packet", consistent with
  the 2022 code).
- A non-playing bot therefore **never needs to register or keepalive with a V3 tunnel**; nobody
  addresses packets to its id.

### V3 UDP packet layout (`V3TunnelCommunicator.CreatePacket`, `V3TunnelCommunicator.cs:275-309`; `ParsePacket` `:622-656`)

```
offset 0  uint32 LE  senderId
offset 4  uint32 LE  receiverId
offset 8  ...        body
```

- **Register**: body empty, `receiverId = 0` (`CreatePacket(localId, 0, Register)`, `:330`).
  Sent to every V3 tunnel the client intends to use (the matchmaking servers, then the agreed
  shortlist, `MatchmakingExchange.cs:183`, `V3PlayerNegotiator.cs:246`) and re-sent every
  `KeepAliveIntervalSeconds` (default 15 s) for every relay in use while in a dynamic lobby and
  in-game (`V3KeepAliveMonitor.cs:164-176`).
- **GameData**: body = the raw game datagram. Distinguished from control packets only by the
  absence of the magic (`:648-655`).
- **Control packets**: body = `"CNCNET"` (6 ASCII bytes) + 1 byte `TunnelPacketType` + payload.
  Types (`:32-67`): `Connected 01`, `PingRequest 02`, `PingResponse 03`, `TunnelChoice 04`,
  `TunnelAck 05`, `NegotiationFailed 06`, `Register 07` (never framed with magic), `GameData 08`,
  `P2PInfo 09`, `P2PDecline 0A`, `KeepAlivePing 0B`, `KeepAlivePong 0C`, `ProbeRequest 0D`,
  `ProbeReport 0E`, `TunnelList 0F`, `TunnelSet 10`.
- Dispatch on receive (`:549-610`): keepalive ping/pong and probe request/report are answered at
  the communicator level regardless of lobby state; other control packets go to the handler
  registered for `(receiverId, senderId)`; game data goes to the handler for `(receiverId, 0)`
  (the bridge).

### The bridge (`V3GameTunnelBridge.cs`)

- Each client reserves one loopback UDP socket on an ephemeral port
  (`TunnelHandler.ReserveLocalGamePort`, `TunnelHandler.cs:462-473`) and writes that port into
  spawn.ini `[Tunnel] Ip=127.0.0.1 Port=<reserved>` (`CnCNetGameLobby.cs:2421-2426`).
- Game -> tunnel (`BridgeWorker`, `V3GameTunnelBridge.cs:193-247`): reads a datagram from the game,
  reads the **big-endian uint16 at game-payload offset 2** as the recipient's `PlayerGameId`
  (`48000 - position`), looks up that player's `V3PlayerInfo.Tunnel`, prefixes
  `[localId LE][recipient.Id LE]` and sends to that tunnel's endpoint.
- Tunnel -> game (`OnTunnelPacketReceived`, `:141-174`): accepts a datagram only if
  `senderId` matches a known player **and it arrived via that player's assigned tunnel**
  (`p.Id == senderId && p.Tunnel == tunnel`), strips the 8-byte header, forwards to the game.
- So in static mode every client sets `Tunnel = the host-selected tunnel` for every peer
  (`ApplyStaticTunnel`, `V3TunnelNegotiationManager.cs:575-582`, and again from the STARTV3
  entry); in dynamic mode each peer has its own negotiated tunnel.

## 4. V3 dynamic mode (`TunnelMode.V3Dynamic`)

### 4.1 Roles

- Negotiation is strictly **pairwise between the two players of each pair**, over UDP, with no
  host involvement. Each client runs one `V3PlayerNegotiator` per remote player in `Players`
  (`V3TunnelNegotiationManager.cs:158-224`). Decider = the side with the smaller `uint` id
  (`V3PlayerNegotiator.cs:177-179`).
- The host's only protocol duties are over IRC: (a) announce the mode (GO field), (b) collect
  `NEGRPT` reports and gate launch on them, (c) optionally send `RENEGALL`, (d) send `STARTV3`.
- The host additionally participates as a **player** only because the stock client is always a
  player: it negotiates its own pairs and runs the launch connectivity probe over its own
  negotiated paths. Neither applies to a bot that is not in `Players` (see 4.9).

### 4.2 When negotiations start (client side, for reference)

- Existing lobby members start negotiating with a newcomer on IRC `JOIN`
  (`Channel_UserAdded` -> `StartNegotiationForPlayerName`, `CnCNetGameLobby.cs:1055`).
- The newcomer starts negotiating with everyone listed in the host's `PO` (player options)
  message (`ApplyPlayerOptions` -> `StartPendingNegotiations`, `:1647-1653`). **So the host must
  send `PO` promptly after a join; without it the newcomer never negotiates.** `PO` rebuilds the
  receiver's `Players` from scratch and drops names not in the IRC channel (`:1550-1579`).
- A mode switch to dynamic resets and restarts all pairs (`ApplyModeTransition`,
  `V3TunnelNegotiationManager.cs:596-610`).

### 4.3 IRC/CTCP messages (`TunnelNegotiationCommands.cs:45-51`)

All CTCPs are IRC `NOTICE <channel> :\x01<text>\x01` (`Online/Channel.cs:278-285`).

**`NEGRPT`** -- negotiation report, sent by **every player** (not the host) whenever its own
view of any pair changes (`BroadcastNegotiationInfo`, `V3TunnelNegotiationManager.cs:424-462`):

```
NEGRPT {hex8 peerId}:{status}[:{pingMs}]|{hex8}:{status}[:{ping}]|...
```

- One entry per remote player the sender has a non-NotStarted directional status for;
  `peerId` is `GeneratePlayerID(peerName)` formatted `x8` (lower-case, zero-padded);
  `status` is `(int)NegotiationStatus`: `0 NotStarted, 1 InProgress, 2 Succeeded, 3 Failed`
  (`NegotiationStatus.cs:61-67`); `pingMs` present only for `Succeeded` with a known pair RTT.
- Queued as `GAME_NEGOTIATION_MESSAGE`, which the send queue coalesces (later report replaces a
  still-queued one; `Online/Connection.cs:1044-1045`, `CnCNetGameLobby.cs:1282-1283`).
- Receiver (`HandleNegotiationReportMessage`, `V3TunnelNegotiationManager.cs:351-374`): parses
  hex id -> name via its own id table, ignores unknown ids and undefined statuses, stores
  `status[sender][target]` and `ping[sender][target]`. It is accepted from **any** sender (no
  host check), including the local player's own echo.
- Re-sent "quietly" on keepalive RTT changes only when the V3 quality tier changes by >= 50 ms
  (`:914-922`, `PingQualityRules.cs:334-336`).

**`RENEGALL`** -- host only (`:1662-1697`, sender must equal `hostName`, and a host ignores its
own copy):

```
RENEGALL {name1},{name2},...
```

- Payload = the host's list of lobby players not currently in game. A receiver not in the list
  ignores it; otherwise it restarts its negotiators with every listed player, clearing those
  pairs and reporting them `InProgress` again (`RestartNegotiations`,
  `V3TunnelNegotiationManager.cs:615-674`). Ignored while the receiver is in game.
- The host sends it from `TriggerRenegotiateAll` (`:1718-1752`) only when no pair among the
  participants is still `InProgress` (locally or per reports), then restarts its own pairs.

**`CHTNL`** -- host only (`HandleTunnelServerChangeMessage`, `:2677-2704`):

```
CHTNL {addr}:{port}
```

- Used in **static/V2 modes** to announce the host-selected tunnel (`:625-628`, `:2720-2721`).
  Receivers resolve it against their own list by exact `Address` and `Port`; if not found they
  enter `tunnelErrorMode` and cannot ready (`:2689-2697`). Not used in dynamic mode.

**Tunnel mode announcement** -- there is no dedicated message. The mode is field
`partIndex + 9` of the host's `GO` (game options) CTCP, as `(int)TunnelMode`
(`OnGameOptionChanged`, `CnCNetGameLobby.cs:1897`; parsed at `:2076-2080` where
`partIndex = (CheckBoxes.Count/32 + 1) + DropDowns.Count`, `:1913-1915`). The receiver applies
it via `HandleTunnelModeChange(mode, false)` (`:2092-2141`): a change to dynamic resets and
restarts all its negotiations; a change away stops them. The initial mode comes from the GAME
broadcast (`[DYN]` vs `addr:port`). A bot must put the same mode in every `GO` it sends, or
every `GO` will flip the clients' mode (defaults to `V3Static` if the field is missing, `:2078`).

**`TNLPNG`** -- see section 6. Not used in dynamic mode (`:450-451`, `:2558-2559`).

Removed: `TNLRENEG` (fork and upstream commit `308f247f`). The loading lobby (saved games) has
no `RENEGALL` handler (`CnCNetGameLoadingLobby.cs:67-74`).

### 4.4 Pair status rule the host applies (`NegotiationDataManager.GetNegotiationStatus`, `NegotiationDataManager.cs:65-87`)

For pair (A,B) with `s1 = status[A][B]`, `s2 = status[B][A]` (explicit `NotStarted` counts as
absent):

1. either is `Failed` -> `Failed`
2. either is `InProgress` -> `InProgress`
3. both `Succeeded` -> `Succeeded`
4. exactly one `Succeeded` -> `InProgress`
5. otherwise `NotStarted`

`AreAllNegotiationsSuccessful(names)` = every unordered pair over `names` is `Succeeded`
(`:185-194`); the manager passes `host.Players` names (`V3TunnelNegotiationManager.cs:464-470`).
Pair ping = first valid of `ping[A][B]`, `ping[B][A]` (`:107-121`). When a player leaves, all
rows/columns for that name are dropped (`ClearPlayer`, `:145-156`; called from
`CnCNetGameLobby.RemovePlayer` -> `_negotiator.RemovePlayer`, `:1065`).

### 4.5 Launch sequence on the host (`HostLaunchGame`, `CnCNetGameLobby.cs:1140-1233`)

1. Refuse while a connectivity check is running (`:1142-1146`).
2. Refuse if `!AreAllNegotiationsSuccessful()`: notice listing failed or incomplete pairs
   (`:1148-1176`).
3. Reserve the local game port (`:1199`), then `BeginLaunchConnectivityCheck(FinishV3DynamicLaunch)`
   (`:1204-1211`).
4. `FinishV3DynamicLaunch` (`:1228-1233`): send `STARTV3`, then `StartGame` (which sends the
   `STRTD` CTCP, `:2408`).

**`BeginLaunchConnectivityCheck`** (`V3TunnelNegotiationManager.cs:485-567`) is **UDP, not IRC**:

- `KeepAliveMonitor.ProbeAllPairsAsync(timeoutMs=6000, resendIntervalMs=1000)`
  (`V3KeepAliveMonitor.cs:358-420`): in parallel (a) `ProbeTargetsAsync(3000, 1000)` sends
  `KeepAlivePing` (payload = 8-byte LE Stopwatch timestamp) to each of the host's own negotiated
  peers and waits for `KeepAlivePong`; (b) sends `ProbeRequest` (empty payload) to each peer over
  the host's negotiated path every 1 s until a `ProbeReport` arrives (payload = 4-byte LE ids of
  the peers *that* client could not reach; empty = all good) or 6 s elapse.
- Result handling (`:513-567`): any local non-responder, any missing report, or any reported
  remote failure -> "Launch aborted." and no STARTV3. Then `AreAllNegotiationsSuccessful` is
  re-checked. If the monitor has **no targets** (`_targets == null || Count == 0`), the probe
  returns an empty result immediately and the launch proceeds (`V3KeepAliveMonitor.cs:361-363`).
- Peers answer `ProbeRequest` by probing their own keepalive targets (3 s) and replying with
  `ProbeReport`, cached for `ProbeReplyCacheSeconds` (5 s) (`:425-469`).

### 4.6 The pairwise UDP negotiation (what the players do; the host does not take part)

Documented so the bot's timeouts and expectations match. Defaults from
`ClientConfiguration.cs:562-817` (all overridable in `NetworkDefinitions.ini
[V3TunnelNegotiation]` / `[V3Matchmaking]`).

1. **Matchmaking exchange** (`MatchmakingExchange.cs`): both sides `Register` on every official
   v4 server (`:183`); the non-decider sends its whole relay list as `TunnelList` chunks every
   400 ms (`RetryIntervalMs`), the decider intersects, ranks (sum of both ICMP pings, capacity
   < 85 %, 1 diversity slot, 6 candidates) and answers `TunnelSet`. Timeout 10 s
   (`ExchangeTimeoutMs`), after which each side ranks locally (8 candidates, 4 of them chosen by a
   pair-seeded hash so both sides still overlap). Wire encodings: `TunnelShortlist.cs:124-213`
   (tunnel key = FNV-1a over `addr:port`, `:94-109`).
2. **Relay round**: both `Register` on the shortlist (`V3PlayerNegotiator.cs:246`). Non-decider
   sends `Connected` to each shortlisted tunnel every 500 ms until it gets a `PingRequest` there,
   for at most 15 s (5 s when the shortlist was agreed) per tunnel (`:633-660`), total 20 s
   (`NonDeciderTotalTimeoutMs`). Decider, per tunnel on which `Connected` arrived, sends 10
   `PingRequest` (payload int32 LE id) with 1 s timeout each (`:663-703`), picks the tunnel with
   the lowest `avgRtt + loss% * 10` once 80 % of tunnels finished (`V3PlayerInfo.cs:232-244`,
   `:519-520`), sends `TunnelChoice` through the winner with payload
   `int32 rttMs, int32 loss‰, byte p2pEnabled` (`:905-911`), retried every 1 s up to 10 times
   until `TunnelAck` (payload `01, p2pEnabled`) arrives on the same tunnel (`:838-853`). The
   non-decider adopts whichever tunnel the choice arrived on (`:823`). `NegotiationFailed`
   (broadcast to all shortlisted tunnels, `:608-609`) tells a decider to stop retrying.
3. **Optional P2P round** if both flagged P2P: exchange `P2PInfo` (6-byte IPv4+port BE entries)
   over the relay, punch, repeat step 2 over the direct paths with 6 pings / 1 s
   (`:1052-1143`). STUN uses the cncnet-server variant on UDP 3478 (48-byte request with
   `0x66B6` BE at offset 0; 40-byte reply, bytes 0-5 XOR `0x20`) (`StunHelper.cs:147-193`).
4. **Keepalive** while in the lobby: `KeepAlivePing` every 15 s per negotiated peer, retried every
   5 s when unanswered; 3 misses -> pair reported `Failed` via `NEGRPT`; a later pong restores it
   to `Succeeded` (`V3KeepAliveMonitor.cs:150-237`, `V3TunnelNegotiationManager.cs:879-958`).

Worst-case time for one pair to reach `Succeeded` with both clients healthy is roughly
10 s (matchmaking miss) + 15 s (connect) + 10 s (pings) + 10 s (ack retries); typical is 2-6 s.
A pair involving a peer that never answers ends `Failed` on both sides after ~20-45 s.

### 4.7 What the host needs to build STARTV3 in dynamic mode

Only: the ordered player list and the channel name. Payload entries are
`GeneratePlayerID(name);name;0.0.0.0:0` (`V3TunnelNegotiationManager.cs:807,820-822`). Receivers
in dynamic mode ignore the address and use their own negotiated per-peer tunnel (`:777-782`).
The host does **not** need to know which tunnel each pair chose.

### 4.8 Reports a bot must keep per player pair

Two directional cells per pair: `status[reporter][target]`, `ping[reporter][target]`, filled
from `NEGRPT`, plus the id->name table. Every report entry is relative to the *sender* of the
CTCP, so the bot needs the IRC sender nick.

### 4.9 Can a non-playing bot host run dynamic mode? Yes, with one condition

- **Condition: the bot must not appear in the clients' `Players` list.** Clients only ever
  negotiate with names in `Players`; `Players` is populated on the client by (a) IRC `JOIN`
  events after the client joined (`CnCNetGameLobby.cs:1006-1056`) and (b) the host's `PO`
  message, which rebuilds the list from scratch (`:1550-1645`). The host is already in the
  channel when a client joins, so (a) never adds it; if the bot omits itself from every `PO`,
  no client will have it in `Players`, nobody will negotiate with it, and no pair involving it
  will exist in anyone's `AreAllNegotiationsSuccessful`. (If the bot *were* listed, every client
  would run a negotiator against it, time out after ~20-45 s, show "Tunnel negotiation with
  <bot> failed", report the pair `Failed`, and the launch gate could never pass without the bot
  implementing the full UDP decider/non-decider protocol of 4.6 -- in both roles, because the
  role depends on id ordering.)
- With that, the dynamic protocol on the host is **pure IRC bookkeeping**:
  1. Broadcast `[DYN]` in `GAME`, send `GO` with tunnel-mode field `1` (and keep sending `1`).
  2. Send `PO` after every join/leave/option change (standard host duty) so newcomers start
     negotiating with the current roster.
  3. Parse every `NEGRPT`, maintain the matrix of 4.8, compute pair status per 4.4 over the
     **real players only**.
  4. Launch gate: all pairs `Succeeded`. Skip `BeginLaunchConnectivityCheck` (it would be a no-op
     anyway with no keepalive targets); instead treat recent keepalive-driven `Failed` reports as
     the liveness signal.
  5. Send `STARTV3 {gameId};{id};{name};0.0.0.0:0;...` in the same order as the last `PO`, then
     `STRTD`.
  6. Optional recovery: `RENEGALL name1,name2,...` with all non-in-game real players, after
     clearing those pairs locally; the stock client only sends it when no participant pair is
     `InProgress`.
- Parts of the protocol that **would** require the bot to answer UDP, none of which are reached
  when it is not a player: `Connected/PingRequest/PingResponse/TunnelChoice/TunnelAck`
  (negotiation), `TunnelList/TunnelSet` (matchmaking), `KeepAlivePing/Pong` (keepalive, 3 misses
  -> the peer reports the pair `Failed`), `ProbeRequest/ProbeReport` (launch check), `Register`
  (tunnel mapping), STUN.
- Caveats for the caller (outside tunnel scope, UNVERIFIED): all players will have spawn.ini
  `Host=No` since `IsHost` is false on every real client (`CnCNetGameLobby.cs:2434`); whether the
  spawner needs exactly one `Host=Yes` is a game-side question. The `GAME` broadcast player list
  and `PO` can simply omit the bot, but the bot will still be an IRC channel member and channel
  op; the loading lobby, `RETURN`/`STRTD` handling and `IsInGame` tracking all key off names in
  `Players`, so a non-listed bot is invisible to them.

## 5. spawn.ini `[Tunnel]` per mode (what the players' clients write)

`WriteSpawnIniAdditions`, `CnCNetGameLobby.cs:2413-2436`:

| mode | `[Tunnel] Ip` | `[Tunnel] Port` | `[Settings] Port` | `[OtherN] Port` |
|---|---|---|---|---|
| V2Legacy | `CurrentTunnel.Address` | `CurrentTunnel.Port` | own V2 id from STARTV2 | peer V2 id from STARTV2 |
| V3Static | `127.0.0.1` | client's reserved loopback port | `48000 - ownPos` | `48000 - peerPos` |
| V3Dynamic | `127.0.0.1` | client's reserved loopback port | `48000 - ownPos` | `48000 - peerPos` |

Plus `[Settings] GameID=<UniqueGameID>`, `Host=<IsHost>`, `[OtherN] Ip=0.0.0.0`
(`GameLobbyBase.cs:1819-1826,1944`). In the V3 modes the game only ever talks to the local
bridge; the bridge resolves `48000-pos` -> player -> tunnel. So for a working game the bot's
START message must (V2) carry ids the tunnel just allocated and the tunnel's IP, or (V3 static)
carry an `addr:port` present in every client's master list, or (V3 dynamic) carry anything
(`0.0.0.0:0`) with the correct names in the correct order.

The V3 bridge is started by `StartGame` only when `_tunnelMode == V3Dynamic ||
CurrentTunnel?.Version == 3` (`CnCNetGameLobby.cs:2392-2406`); if the local port cannot be
reserved the client does not launch.

## 6. `TNLPNG` and other periodic tunnel traffic

- `TNLPNG {ms}` (`int`, `-1` = unknown) is sent by **each player about itself** in V2/V3 static
  modes: on join (`OnJoined` -> `UpdatePing`, `:444,448-453`), whenever the current tunnel is
  re-pinged (every 20 s cycle, `TunnelHandler_CurrentTunnelPinged`, `:361`), and after a `CHTNL`
  (`HandleTunnelServerChange` -> `UpdatePing`, `:2751`). It is the sender's **ICMP RTT to the
  tunnel**, not a pair ping. Receivers just set that player's ping indicator (`:2556-2567`);
  unknown senders are ignored, and in dynamic mode the message is ignored entirely.
- The host sends nothing else periodically about tunnels. The `GAME` broadcast (every broadcast
  interval, `:3095-3157`) carries the tunnel field (`[DYN]` or `addr:port`), which is what the
  lobby list uses to decide whether a game is joinable (section 0).
- A bot in static/V2 mode may send `TNLPNG -1` or nothing; if it is not in `Players` the message
  is dropped by receivers anyway.

## 7. Fallbacks and error handling on the players' side

- **STARTV2 tunnel mismatch**: the receiver switches to the host's tunnel if a v2 tunnel with that
  IP exists in its list (port is not compared); otherwise it refuses to start
  (`CnCNetGameLobby.cs:2270-2292`). An unknown player name silently aborts (`:2294-2297`).
- **STARTV3 problems**: wrong part count, unparsable game id or id, unknown name, or (static
  mode) unresolvable `addr:port` -> `NotifyStartFailed` notice, no launch (`:2328-2358`,
  `V3TunnelNegotiationManager.cs:752-788`). A receiver still in a running game ignores STARTV3
  (`:2315-2320`).
- **CHTNL to an unlisted tunnel**: receiver enters `tunnelErrorMode`, cannot ready/launch until a
  valid `CHTNL` arrives (`:2689-2697`, `:2756-2760`).
- **GAME broadcast with an unlisted tunnel**: the game is not shown to that client at all
  (`CnCNetLobby.cs:1641-1644`); a tunnel in maintenance or de-listed from the master list thus
  silently hides the game. Dynamic mode has no such dependency.
- **Dynamic-mode timeouts** (defaults): matchmaking 10 s; connect phase 15 s (5 s + up to 10 s
  extension when the shortlist was agreed, `V3PlayerNegotiator.cs:434-449`); decider ping phase
  12 s; non-decider total 20 s; tunnel-choice ack 10 x 1 s; P2P candidate exchange 3 s, P2P
  non-decider 10 s. Keepalive: 15 s interval, 5 s tick, 3 misses (~15-25 s) -> `Failed`
  report, auto-restored on the next pong. A pair with no usable V3 tunnels at all is reported
  `Failed` immediately (`V3TunnelNegotiationManager.cs:169-185`).
- **Host launch gate failures** are host-local: notices only, no message to players
  (`CnCNetGameLobby.cs:1148-1176`, `V3TunnelNegotiationManager.cs:553-563`).
- **Renegotiation guard**: `RENEGALL` is ignored by in-game receivers and by receivers not in the
  payload list (`:1670-1689`); restarts never touch a pair while the local game runs
  (`V3TunnelNegotiationManager.cs:620-624`).
- **Player leaves**: all its pair data is dropped on every client (`RemovePlayer`, `:1058-1086`);
  the host's gate recomputes over the remaining names.

## UNVERIFIED items

- Current production `cncnet-server` behaviour for V3 `Register`, mapping expiry (30 s in the
  2022 source) and whether forwarding requires the receiver to have sent at least one packet;
  the 2022 code is the only server source on this machine.
- Whether the live master list ever returns a non-zero `distance` (sample showed `0`), and what
  `version=5` denotes.
- Game-side consequence of no client writing `[Settings] Host=Yes` when the host is not a player.

Verified (not an open item): a joining client never adds pre-existing channel members to
`Players`. The IRC NAMES reply goes through `Channel.OnUserListReceived` (`Online/Channel.cs:132-156`),
which raises only `UserListReceived`, and the lobby's handler for it (`CnCNetGameLobby.cs:989-1004`)
does not touch `Players`; `UserAdded` is raised only from `OnUserJoined` (`Channel.cs:112-115`).
So `Players` on a client = IRC `JOIN`s after it joined + whatever the host's `PO` lists.
