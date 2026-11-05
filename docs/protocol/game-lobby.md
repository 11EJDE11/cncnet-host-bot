# CnCNet game-room (game lobby) protocol — host-side specification

Scope: everything a standalone bot must send and understand to act as the **host** of a
CnCNet game room (the IRC channel a game is set up in) for the YR client package, without the
client UI. Tunnel negotiation (NEGRPT and the UDP side) is covered by another document and is
only named here.

Sources (all line numbers refer to these exact files):

- `C:\Users\QWE\source\repos\11EJDE11\xna-cncnet-client` @ `64cfd548` (branch `develop`)
  - `CGL` = `DXMainClient/DXGUI/Multiplayer/GameLobby/CnCNetGameLobby.cs`
  - `MGL` = `DXMainClient/DXGUI/Multiplayer/GameLobby/MultiplayerGameLobby.cs`
  - `GLB` = `DXMainClient/DXGUI/Multiplayer/GameLobby/GameLobbyBase.cs`
  - `CH` = `DXMainClient/Online/Channel.cs`, `CONN` = `DXMainClient/Online/Connection.cs`
  - `LOBBY` = `DXMainClient/DXGUI/Multiplayer/CnCNet/CnCNetLobby.cs`
  - `FHC` = `DXMainClient/Online/FileHashCalculator.cs`
  - `MAPSHARER` = `DXMainClient/Domain/Multiplayer/CnCNet/MapSharer.cs`
  - `V3NM` = `DXMainClient/Domain/Multiplayer/CnCNet/V3TunnelNegotiationManager.cs`
- `C:\Users\QWE\source\repos\cncnet-yr-client-package\package` @ `320ba9a2` (`PKG`)
  - `PKG/Resources/GameOptions.ini`, `PKG/Resources/ClientDefinitions.ini`,
    `PKG/Resources/GameLobbyBase.ini`, `PKG/Resources/MultiplayerGameLobby.ini`,
    `PKG/Resources/CnCNetGameLobby.ini`, `PKG/INI/MPMaps.ini`, `PKG/Resources/FHCConfig.ini`

Everything not marked **UNVERIFIED** was read directly from the code above. Items marked
UNVERIFIED are inferences that were not confirmed by a running client.

---

## 0. Transport conventions

### 0.1 CTCP wire format

Every lobby command is an IRC NOTICE to the game channel whose text is wrapped in `\x01`:

```
NOTICE <#channel> :\x01<command text>\x01          CH:278-285
```

`Channel.SendCTCPMessage(message, qmType, priority, replace)` builds exactly
`"NOTICE " + ChannelName + " " + ':' + '\x01' + message + '\x01'` (CH:278-285).
Receivers accept the same payload delivered as `PRIVMSG` too (CONN:716-719), except when the
text contains `ACTION`. On receive, `Connection` strips the leading `\x01` and the trailing
`\x01` (`ctcpMessage.Remove(0,1).Remove(len-2)`, CONN:679) and takes the sender nick from the
prefix (everything before `!`). The channel name must be the game channel; otherwise the
message is treated as a private CTCP (CnCNetManager.DoCTCPParsed:380-399).

### 0.2 Handler matching rules (receiver side)

`CnCNetGameLobby.Channel_CTCPReceived` (CGL:1105-1119) walks `ctcpCommandHandlers` in array
order and stops at the first handler whose `Handle()` returns true. Matching rules
(`DXMainClient/DXGUI/Multiplayer/GameLobby/CommandHandlers/*.cs`):

| Handler type | Matches when | Payload |
|---|---|---|
| `NoParamCommandHandler` | `message == CommandName` | none |
| `NotificationHandler` | `message == CommandName` | none; outer action checks sender |
| `StringCommandHandler` | `message.Length >= Name.Length+1 && message.StartsWith(Name)` | `message.Substring(Name.Length+1)` (one separator char skipped, usually a space) |
| `IntCommandHandler` | same StartsWith + `int.TryParse(Substring(Name.Length+1))` succeeds | int |
| `IntNotificationHandler` | `message.StartsWith(Name)` (no length check; parse failure gives 0) | int |

Consequences: `StartsWith` is not word-bounded, so `"R"` is tried against `"RETURN"` and
`"RENEGALL ..."` and only falls through because the int parse fails. Never invent a command
whose name is a prefix of another one, and always put exactly one space between command and
payload.

### 0.3 Send queue and pacing (what a bot should imitate)

`Connection.QueueMessage` (CONN:1020-1061): `INSTANT_MESSAGE` is written to the socket
immediately; the "special" types `GAME_BROADCASTING_MESSAGE, GAME_PLAYERS_MESSAGE,
GAME_SETTINGS_MESSAGE, GAME_PLAYERS_READY_STATUS_MESSAGE, GAME_LOCKED_MESSAGE,
GAME_GET_READY_MESSAGE, GAME_NOTIFICATION_MESSAGE, GAME_HOSTING_MESSAGE, WHOIS_MESSAGE,
GAME_CHEATER_MESSAGE, GAME_NEGOTIATION_MESSAGE` replace any message of the same type already
waiting in the queue (CONN:1068-1090); all other types are inserted by priority (higher
first). The send thread sleeps `SendSleep` ms between messages (CONN:911), default **2500 ms**
(`ClientConfiguration.SendSleep`, ClientConfiguration.cs:219; the YR package does not override
it). Delayed messages (`JOIN`/`PART` on persistent channels) are not used for game channels.

Practical rule for the bot: coalesce PO / GO / notification bursts the same way (only the last
one of each kind matters) and keep roughly 2.5 s between NOTICEs to avoid GameSurge flood
kicks; `KICK`/`MODE` may go out immediately.

Message types used by each command are given in the catalogue below as `type/priority`.

### 0.4 Index spaces (YR package)

From `GLB.InitPlayerOptionDropdowns` (GLB:1140-1291) and `PKG/Resources/GameOptions.ini`:

- `Sides=America,Korea,France,Germany,Great Britain,Libya,Iraq,Cuba,Russia,Yuri` → `SideCount = 10`
  (GLB:1162).
- `[RandomSelectors] Allied=0,1,2,3,4 / Soviet=5,6,7,8 / Ladder=0,1,2,4,6,6,6,6,9,9,9,9` → 3
  selectors, `RandomSelectorCount = 3 + 1 = 4` (GLB:1166; GLB:1376-1400 drops selectors with
  < 2 valid sides).
- **SideId**: `0` = Random, `1..3` = Allied / Soviet / Ladder random groups, `4..13` =
  America..Yuri (`sideIndex + RandomSelectorCount`), `14` = Spectator
  (`SideCount + RandomSelectorCount`, GLB:1441). The side drop-down has 15 items; code often
  writes "spectator" as `ddPlayerSides[0].Items.Count - 1`.
- **ColorId**: `0` = Random, `1..8` = `[MPColors]` in file order Yellow, Red, Blue, Green,
  Orange, Cyan, Purple, Pink (`MPColors.Count = 8`; the 4th value is the in-game colour index).
- **StartingLocation**: `0` = random (`???`), `1..N` = waypoint `N-1` of the map.
- **TeamId**: `0` = none, `1..4` = A, B, C, D (`ProgramConstants.TEAMS`).
- **AILevel** (in PO): `0` = "Easy AI", `1` = "Medium AI", `2` = "Hard AI"
  (`ProgramConstants.AI_PLAYER_NAMES`); spawn.ini handicap = `|AILevel-2|` (PlayerInfo.cs:60).
- **Difficulty drop-down** (`ddDifficulty`): not present in the YR INI → always `-1` on the wire.
- `MAX_PLAYER_COUNT = 8` (GLB:47) is the hard cap for humans + AI.

---

## 1. Room creation (what the host does before anyone can join)

`LOBBY.Gcw_GameCreated` (LOBBY:1086-1114) and `CGL.SetUp`/`OnJoined` (CGL:297-369, 412-446):

1. Channel name: `"#cncnet-yr" + "-game" + random.Next(1000000, 9999999)` →
   `#cncnet-yr-game1234567` (LOBBY:1167-1178; chat channel from GameCollection.cs:133). Must
   not collide with a listed game.
2. Password: custom password if the host typed one, else
   `Utilities.CalculateSHA1ForString(channelName).Substring(0, 10)` = first 10 lowercase hex
   chars of SHA1(UTF-8 channel name) (LOBBY:1096-1098). Joiners compute the same default
   (LOBBY:951-952).
3. `JOIN <channel> <password>` (INSTANT, LOBBY:1106-1107).
4. On the host's own JOIN echo (`Channel_UserAdded` with own nick, CGL:1006-1056) the client's
   `Players[0]` becomes the host with `Ready = true`; then `OnJoined()` (CGL:412):
   - `MODE <channel> +klnNs <password> <playerLimit>` (SYSTEM/50, CGL:421-424)
   - `TOPIC <channel> :<CNCNET_PROTOCOL_REVISION>;<localGame lower>` → `TOPIC #... :B;yr`
     (CGL:426-429; revision `"B"` ProgramConstants.cs:29)
   - starts the `GAME` broadcast timer: first broadcast after 10 s, then every 30 s
     (`INITIAL_GAME_BROADCAST_DELAY = 10.0`, `GAME_BROADCAST_INTERVAL = 30.0`, CGL:37-39; lock,
     unlock and settings changes accelerate the next broadcast to ≤10 s, CGL:3089-3093).
     Game-list entries expire after **35 s** without a refresh (GameListBox.cs:56), so the bot
     must rebroadcast at least every 30 s.
   - `RandomSeed = random.Next()` and `GenerateGameID()` already happened in `SetUp`/`Refresh`
     (CGL:327, MGL:660).
5. Inactivity: with `InactiveHostWarningMessageSeconds=900` and `InactiveHostKickSeconds=300`
   (PKG ClientDefinitions.ini) the client closes the room after 1200 s without mouse movement
   unless the room has a custom password (GameHostInactiveChecker.cs, CGL:402-408). A bot has
   no such obligation; it is a client-side convenience.

### 1.1 `GAME` broadcast (host → `#cncnet-yr-games`)

`CGL.BroadcastGame` (CGL:3095-3154), sent as CTCP on the broadcast channel
(SYSTEM/20). Exactly **15** `;`-separated fields; receivers drop anything else
(LOBBY:1578). Field list (0-based):

```
GAME <0 rev>;<1 gameVersion>;<2 playerLimit>;<3 channelName>;<4 roomName>;<5 flags>;<6 players>;<7 mapName>;<8 gameModeUIName>;<9 tunnel>;<10 loadedGameId>;<11 skillLevel>;<12 mapSha1>;<13 packedOptions>;<14 difficulty>
```

- `0` `"B"` (must equal the receiver's revision or the game is ignored, LOBBY:1600).
- `1` `ProgramConstants.GAME_VERSION` (the package version string; a mismatch marks the game
  "Incompatible" and `DisallowJoiningIncompatibleGames=true` in the YR package blocks joining).
- `2` max players (`playerLimit`).
- `3` channel name, `4` room display name.
- `5` five chars, each `0`/`1`: `locked`, `isCustomPassword`, `closed`, `IsLoadedGame`(0),
  `IsLadder`(0). `closed=1` makes receivers remove the game (LOBBY:1725-1737); the client
  sends it once from `LeaveGameLobby` (CGL:876-887).
- `6` comma-separated player names (the client always includes itself since it is `Players[0]`;
  note the client strips the last char assuming at least one name — a bot with 0 players
  must still emit an empty field, keeping 15 fields).
- `7` `Map.UntranslatedName`, `8` `GameMode.UntranslatedUIName` (empty if no map).
- `9` `"[DYN]"` in V3-dynamic mode, else `"<tunnelAddress>:<port>"`; `"0.0.0.0:0"` if none.
  Receivers ignore the game if the address is not in their tunnel list (LOBBY:1636-1640).
- `10` `0`, `11` skill level index (`SkillLevelOptions`, default `Any,Beginner,Intermediate,Pro`).
- `12` `Map.SHA1` (empty if no map).
- `13` `GetPackedGameOptionValuesString()` (GLB:103-126): the `BroadcastToLobby=true`
  check boxes packed as in GO (section 2.5) followed by the `BroadcastToLobby=true` drop-down
  indices, all comma-separated. For the YR package: 8 check boxes (chkShortGame, chkRA2Mode,
  chkBalancePatch, chkNoYuri, chkNoFrance, chkNoSpy, chkNoStolenTech, chkCrates → one int)
  then cmbCredits, cmbSuperWeaponsModifier, cmbCratesModifier, cmbGameSpeedCapMultiplayer →
  `"<int>,<i>,<i>,<i>,<i>"`. Receivers only parse it when `gameVersion` equals theirs.
- `14` `ddDifficulty.SelectedIndex` or `-1`. **Variant**: the user's `live-test-9.3.x` client
  builds do not append field 14 to GAME or the trailing difficulty field to GO (diff
  observed); the target `develop` source does. Check which client the players run.

Host name as seen by joiners = the IRC nick that sent the CTCP (LOBBY:1716). Joiners later
refuse to stay if that nick is not in the channel user list (CGL:989-1002).

### 1.2 Locking

- Lock: `MODE <channel> +i` (INSTANT, CGL:2624-2632); unlock: `MODE <channel> -i`
  (CGL:2634-2644). Non-host clients learn the state from the mode change (`"+i"`/`"-i"`,
  CGL:1088-1103).
- The client auto-locks when `Players.Count >= playerLimit` after a join (CGL:1045-1049) and
  auto-unlocks when a player leaves and not in game (CGL:1072-1075) or after the game
  (CGL:2224-2225).
- `LCKGME` CTCP is a notification sent when the host presses Launch while unlocked
  (section 2.9).

### 1.3 Room settings change `GSETTINGS`

`GSETTINGS <roomName>;<maxPlayers>;<skillLevel>;<isCustomPassword 0|1>` (CGL:750-764,
GAME_SETTINGS_MESSAGE/11). Preceded by `MODE +k`/`-k` and `MODE +l <n>` as needed
(CH:313-347, CGL:654-748). Receivers (CGL:767-812): ignored by the host itself, need ≥4 parts,
**no sender check**. Client quirk: `GSETTINGS`, `GO` and `OR` all use
`GAME_SETTINGS_MESSAGE`, which is a replace-in-queue type (0.3), so a `GSETTINGS` still waiting
in the client's queue is silently dropped by a later `GO`; a bot should send `GSETTINGS` first
and `GO` only after it has gone out.

### 1.4 Kick / ban

- Kick: `KICK <channel> <nick>` (INSTANT/8, CH:292-295; CGL:2646-2655).
- Ban: `MODE <channel> +b *!*@<hostname>` then `KICK` (CH:302-306, CGL:2657-2672). The
  hostname comes from the user's IRC host (available from the JOIN prefix, CONN:697-704, or
  WHO). The bot needs channel-op status, which the first joiner of a fresh channel gets.
- Excess joiners (more than `playerLimit` or 8) are kicked immediately (CGL:1009-1016).

---

## 2. CTCP command catalogue (game channel)

Registered in `ctcpCommandHandlers` (CGL:75-108), in this order:

| # | Command | Handler type | Direction | Sender check | Message type/priority (sender side) |
|---|---|---|---|---|---|
| 1 | `OR <int>` | Int | player → host | host only acts (`IsHost`), player must be in `Players`, ignored while `IsInGame` | GAME_SETTINGS/6 |
| 2 | `R <int>` | Int | player → host | host only acts, player must be in `Players` | GAME_PLAYERS_READY_STATUS/5 |
| 3 | `PO <...>` | String | host → all | `sender == hostName` | GAME_PLAYERS/11 |
| 4 | `PEO <...>` | String | host → all | **none** | GAME_PLAYERS_EXTRA/11, replace=true |
| 5 | `GO <...>` | String | host → all | `sender == hostName` | GAME_SETTINGS/11 |
| 6 | `STARTV2 <...>` | String | host → all | `sender == hostName` | SYSTEM/10 |
| 7 | `STARTV3 <...>` | String | host → all | `sender == hostName` | SYSTEM/10 |
| 8 | `AISPECS` | Notification | host → all | `sender == hostName` | GAME_NOTIFICATION/0 |
| 9 | `GETREADY` | Notification | host → all | `sender == hostName` | GAME_GET_READY/0 |
| 10 | `INSFSPLRS` | Notification | host → all | `sender == hostName` | GAME_NOTIFICATION/0 |
| 11 | `TMPLRS` | Notification | host → all | `sender == hostName` | GAME_NOTIFICATION/0 |
| 12 | `CLRS` | Notification | host → all | `sender == hostName` | GAME_NOTIFICATION/0 |
| 13 | `SLOC` | Notification | host → all | `sender == hostName` | GAME_NOTIFICATION/0 |
| 14 | `LCKGME` | Notification | host → all | `sender == hostName` | GAME_NOTIFICATION/0 |
| 15 | `NVRFY <idx>` | IntNotification | host → all | `sender == hostName` | GAME_NOTIFICATION/0 |
| 16 | `INGM <idx>` | IntNotification | host → all | `sender == hostName` | GAME_NOTIFICATION/0 |
| 17 | `MAPREQ <sha1>` | String | player → host | none (upload only if `IsHost`) | SYSTEM/9 |
| 18 | `MAPFAIL <sha1>` | String | any | behaviour depends on `sender == hostName` | SYSTEM/9 |
| 19 | `MAPOK <sha1>` | String | host → all | `sender == hostName` | SYSTEM/9 |
| 20 | `MAPSDISABLED` | NoParam | player → all | none | SYSTEM/9 |
| 21 | `STRTD` | NoParam | any → all | none | SYSTEM/20 |
| 22 | `RETURN` | NoParam | any → all | none | SYSTEM/20 |
| 23 | `TNLPNG <ms>` | Int | any → all | none (ignored in V3-dynamic) | SYSTEM/10 |
| 24 | `FHSH <sha1>` | String | player → host | host only acts | SYSTEM/10 |
| 25 | `MM <nick>` | String | host → all | `sender == hostName` | GAME_CHEATER/10 |
| 26 | `DR <sides>,<r1>,...` | String | any → all | none | CHAT/0 |
| 27 | `CD` | NoParam | any → all | none | INSTANT/0 |
| 28 | `CHTNL <addr>:<port>` | String | host → all | `sender == hostName` | SYSTEM/10 |
| 29 | `NEGRPT <...>` | String | any → all | (negotiation doc) | GAME_NEGOTIATION/10 |
| 30 | `RENEGALL <n1,n2,...>` | String | host → all | `sender == hostName && !IsHost` | SYSTEM/10 |
| 31 | `GSETTINGS <...>` | String | host → all | none (`!IsHost` only) | GAME_SETTINGS/11 |

`MultiplayerGameLobby` and `GameLobbyBase` register no CTCP handlers of their own; they only
provide the handler bodies (`HandleDiceRollResult`, `ApplyPlayerExtraOptions`).

### 2.1 `PO` — player options (host → all)

Builder `CGL.BroadcastPlayerOptions` (CGL:1480-1518):

```
"PO " + for each p in Players ++ AIPlayers:
    (p.IsAI ? p.AILevel : p.Name) + ";" + packed + ";" + (human only: readyStatus + ";")
```

- Humans first (in `Players` order), then AIs. The string ends with a trailing `;`.
- `packed` = `BinaryPrimitives.ReadInt32LittleEndian([TeamId, StartingLocation, ColorId, SideId])`
  = `TeamId | StartingLocation << 8 | ColorId << 16 | SideId << 24` (signed decimal int;
  SideId ≥ 128 would make it negative but SideId ≤ 14).
- `readyStatus`: `2` if `AutoReady && !IsInGame && !LastMapChangeWasInvalid`, else
  `Convert.ToInt32(Ready)` (`1`/`0`). `HUMAN_PLAYER_OPTIONS_LENGTH = 3`,
  `AI_PLAYER_OPTIONS_LENGTH = 2` (CGL:34-35) are the token counts per entry.
- Example: host `Bot` ready (all zeros), player `Alice` side Soviet-random(2) colour Red(1)
  start 0 team A(1) not ready, one Hard AI (level 2) side 0 colour 0 start 0 team B(2):
  `PO Bot;0;1;Alice;33619969;0;2;2;`
  (Alice: `1 | 0<<8 | 1<<16 | 2<<24 = 33619969`; AI entry: level `2`, packed `2 | 0 | 0 | 0 = 2`,
  no ready field.)

Receiver `ApplyPlayerOptions` (CGL:1538-1626): requires `sender == hostName`; clears
`Players` and `AIPlayers`, re-reads them from the message (so the message is the authoritative
roster), keeps previously known ping and `IsInGame` per name; splits on `;` with
`RemoveEmptyEntries`; a token that parses as an int is an AI level (so a purely numeric nick
would be misread); humans not found in `channel.Users` are skipped (3 tokens); any field that
fails parse or range (`side 0..14`, `color 0..8`, `start 0..8`, `team 0..4`) aborts the whole
message silently; AIs get `Ready = true`. The receiver sets its own Launch button text from its
own entry's ready flag.

Host must send `PO` after: a player joins (CGL:1033), leaves/quits/is kicked (CGL:1066; kick
path only updates UI, CGL:976-987 — UNVERIFIED whether a PO follows a kick; the bot should
send one), any `OR` accepted (CGL:1457), any `R` (CGL:1476), host-side option edits
(MGL:997-1000), starting-location edits (MGL:772), and after the game returns (CGL:2219).

### 2.2 `OR` — options request (player → host)

Sender (CGL:1338-1352): `"OR " + ReadInt32LittleEndian([side, color, start, team])` =
`side | color << 8 | start << 16 | team << 24`. Note the byte order differs from PO.

Host validation `HandleOptionsRequest` (CGL:1387-1458), all silent rejects:

1. `IsHost`, not `ProgramConstants.IsInGame`, sender in `Players`.
2. `0 <= side <= SideCount + RandomSelectorCount` (≤14), `0 <= color <= MPColors.Count` (≤8).
3. Build `disallowedSides = [false × RandomSelectorCount] ++ GetDisallowedSides()` where
   `GetDisallowedSides()` (GLB:1605-1632) marks sides disallowed by the coop map info, the game
   mode's `DisallowedPlayerSides`, and any **checked** check box with `DisallowedSideIndex`/
   `DisallowedSideIndices` (YR: `chkRA2Mode` → 9 (Yuri), `chkNoYuri` → 9, `chkNoFrance` → 2).
   Reject if `0 < side < SideCount && disallowedSides[side]` (note: this compares the raw
   drop-down index against the padded array; side indices ≥ SideCount are not checked here).
4. Coop maps: reject if `CoopInfo.DisallowedPlayerSides.Contains(side-1)` or side == spectator;
   reject if `CoopInfo.DisallowedPlayerColors.Contains(color-1)`.
5. `start == 0 || GameModeMap.AllowedStartingLocations.Contains(start)` (null map → allowed).
6. `0 <= team <= 4`.
7. If side, start or team changed → `ClearReadyStatuses()` (all non-host, non-autoready
   players become not ready; colour changes do not clear readiness).
8. Apply, then `BroadcastPlayerOptions()`.

Colour uniqueness is not checked here (only at launch, `CLRS`).

### 2.3 `R` — ready status (player → host)

Sender `RequestReadyStatus` (CGL:1355-1385): `"R 2"` if auto-ready is on, `"R 1"` if currently
not ready, `"R 0"` if currently ready (toggle). With no valid map a client with auto-ready
sends `"R 0"`.
Host `HandleReadyRequest` (CGL:1460-1478): `Ready = v > 0; AutoReady = v > 1;` then PO.

Host-side readiness clearing (`GLB.ClearReadyStatuses`, GLB:2380-2387): for `i = 1..` (index
0 assumed to be the host) set `Ready = false` unless the player has `AutoReady` and is not in
game (or `resetAutoReady` is passed, which happens when the map becomes invalid, MGL:1182).
Triggered by: any game option change (MGL:983-989), map change (MGL:1178-1192), tunnel change
(CGL:638), frame-rate/protocol commands, OR changes, game exit (GLB:2289), kicks (CGL:986).

### 2.4 `PEO` — player extra options (host → all)

`PlayerExtraOptions.ToCncnetMessage()` (PlayerExtraOptions.cs:44-61):

```
PEO <s><c><t><r><m>;<mapping list>
```

five `'0'/'1'` chars = IsForceRandomSides, IsForceRandomColors, IsForceNoTeams,
IsForceRandomStarts, IsUseTeamStartMappings; then `;` and `TeamStartMapping.ToListString`
= comma-joined team tokens per start slot 1..N (`"x"` = blocked slot, `"-"` = no team,
`"A".."D"`; TeamStartMapping.cs:12-14, 44-45). Default: `PEO 00000;`.
Receiver (GLB:702-726 via `FromMessage`, PlayerExtraOptions.cs:62-80): **no sender check**;
needs ≥2 parts and ≥5 chars; throws (unhandled) on a malformed message. Host sends it on
join (CGL:1034), when the panel changes (CGL:1520-1535), and after the game (CGL:2220). When
`UseTeamStartMappings` is set the mapping must pass `GetTeamMappingsError` before launch
(distinct starts, ≥2 distinct teams; PlayerExtraOptions.cs:28-42).

### 2.5 `GO` — game options (host → all)

Builder `CGL.OnGameOptionChanged` (CGL:1856-1900), `ExtendedStringBuilder("GO ", true, ';')`
(separator between fields, trailing one removed):

```
GO <packedCheckBoxInt>[;<packedCheckBoxInt>...];<dd0>;<dd1>;...;<mapOfficial>;<mapSha1>;<gameModeName>;<frameSendRate>;<maxAhead>;<protocolVersion>;<randomSeed>;<removeStartingLocations>;<mapUntranslatedName>;<tunnelMode>;<difficulty>
```

- Check boxes: `Conversions.BoolArrayIntoBytes` (Conversions.cs:160-190): check box `k` sets bit
  `k % 8` of byte `k / 8`; bytes are zero-padded to a multiple of 4 and read as little-endian
  int32s. Net effect: check box `k` (0-based, in `CheckBoxes` order) is bit `k` of packed int
  `k / 32`. Sender emits `ceil(ceil(n/8)/4)` ints; receiver expects `n/32 + 1` ints
  (CGL:1912) — equal for n ≤ 31.
- Drop-downs: `SelectedIndex` of each `DropDowns` entry, in order (CGL:1884-1885).
- Trailing fields (CGL:1887-1897): `Convert.ToInt32(Map.Official)` (`0|1`), `Map.SHA1`,
  `GameMode.Name` (internal section name, e.g. `Battle`), `FrameSendRate`, `MaxAhead`,
  `ProtocolVersion`, `RandomSeed`, `Convert.ToInt32(RemoveStartingLocations)`,
  `Map.UntranslatedName`, `(int)tunnelMode` (`0`=V3Static, `1`=V3Dynamic, `2`=V2Legacy,
  TunnelMode.cs), `ddDifficulty.SelectedIndex` or `-1`.
- Defaults (MGL:132-134, ClientConfiguration.cs:836-846): `FrameSendRate=7`, `MaxAhead=0`,
  `ProtocolVersion=2` unless `ClientDefinitions.ini` overrides (the YR package does not).
  Note `[ForcedSpawnIniOptions]` in GameOptions.ini forces `Protocol=0` and `FrameSendRate=2`
  into spawn.ini afterwards (GLB:1825-1834), so the GO values only matter for the lobby.

Receiver `ApplyGameOptions` (CGL:1906-2090): `sender == hostName`; computes
`partIndex = (CheckBoxes.Count/32 + 1) + DropDowns.Count` and requires
`parts.Length >= partIndex + 11` (else red "invalid game options message" notice and nothing
applied). Then, in this order: FrameSendRate/MaxAhead/ProtocolVersion (notices on change);
map lookup by `(GameMode.Name == gameMode && Map.SHA1 == mapSHA1)` → `ChangeMap(found)` or
`ChangeMap(null)` + map-sharing request (section 2.7); check boxes (bit by bit, notice per
change); drop-downs (an index `< -1 || >= Items.Count` is skipped silently); RandomSeed;
`RemoveStartingLocations`; tunnel mode (default V3Static if absent); difficulty only if
`GameMode.UseDifficultyDropDown`. Unknown map ⇒ the client has `Map == null`, cannot ready up
normally, and `STARTV2/3` aborts with a `RETURN` (CGL:2237-2241, 2323-2327).

**Option counts for the YR package** (derived from INI control creation order; the counts
must match the players' client exactly or they show "invalid game options message"):

Control registration (`GameLobbyCheckBox.Initialize` → `CheckBoxes.Add`, GameLobbyCheckBox.cs:56;
`GameLobbyDropDown` → `DropDowns.Add`, GameLobbyDropDown.cs:30) happens while
`INItializableWindow.ReadINIForControl` walks the `$CC*` keys of the window section depth-first
(INItializableWindow.cs:148-162). Section key order: `[MultiplayerGameLobby]` is the
`CnCNetGameLobby.ini` window section (`IniNameOverride`, CGL:245) with
`$BaseSection=GameLobbyBase`; `CCIniFile` inserts the base section's missing keys **in front**
(CCIniFile.cs:9-33), and `BasedOn` merging appends new keys of an existing section at the end
(IniFile.ConsolidateIniFiles → AddOrReplaceKey, IniSection.cs:38-48).

`CheckBoxes` (18) in `PKG/Resources/GameLobbyBase.ini` order:

| bit | Name | spawn.ini / map code (the "spawn ini code") | default |
|---|---|---|---|
| 0 | chkShortGame | `[Settings] ShortGame=True/False` | True |
| 1 | chkRedeplMCV | `MCVRedeploy` | True |
| 2 | chkAutoRepair | map code `INI\Game Options\Auto Repair.ini` | False |
| 3 | chkMultiEng | `MultiEngineer` | False |
| 4 | chkIngameAllying | `AlliesAllowed` + map code `Allies Allowed.ini` | True |
| 5 | chkDestrBridges | `BridgeDestroy` | True |
| 6 | chkBuildOffAlly | `BuildOffAlly` | True |
| 7 | chkRA2Mode | `Ra2Mode`; DisallowedSideIndex=9 | False |
| 8 | chkBalancePatch | map code `Yuri Rebalance Patch.ini` | False |
| 9 | chkNoSpawnPreviews | map code `No Spawn Previews.ini` | False |
| 10 | chkDisableGameSpeed | `DisableGameSpeed` | False |
| 11 | chkNoYuri | map code `No_Yuri.ini`; DisallowedSideIndices=9 | False |
| 12 | chkNoFrance | map code `No_France.ini`; DisallowedSideIndices=2 | False |
| 13 | chkNoSpy | map code `No_Spy.ini` | False |
| 14 | chkNoStolenTech | map code `No Stolen Tech.ini` | False |
| 15 | chkNoDogEngiEat | map code `No Dog Engi Eat.ini` | False |
| 16 | chkCrates | `Crates` | False |
| 17 | chkAutoSave | `AutoSaveInterval=7200/0` (window-level `$CC21`) | False |

(`DefaultValue=` keys in the INI are not parsed by any class — only `Checked=` is
(GameSessionCheckBox.cs:135-138, XNACheckBox.cs:171-173) — so those boxes default to
unchecked.) `chkRecordReplay` (`$CC32`, type `LocalGameLobbyCheckBox`) is **not** in
`CheckBoxes`: that type does not exist in the `develop` source at all (the YR package targets
the user's `live-test-9.3.x` client where it registers into a separate `LocalCheckBoxes` list),
so it never affects the GO bit count either way.

`DropDowns` (6) in order: `cmbCredits` (Items 100000,30000,20000,15000,12500,10000,7500,5000,2500;
DefaultIndex 5; `Credits=<text>`), `cmbStartingUnits` (10..0; DefaultIndex 10; `UnitCount`),
`cmbSuperWeaponsModifier` (4 map-code INIs; 0), `cmbCratesModifier` (4; 1), `cmbAIModifier`
(4; 0), `cmbGameSpeedCapMultiplayer` (7 items `60 FPS..10 FPS`; `GameSpeed=<index>`;
DefaultIndex 0; appended last because `MultiplayerGameLobby.ini` adds `$CC-GODD03` to
`[GameOptionsPanel]`).

Therefore a YR `GO` has `1 + 6 + 11 = 18` fields and `partIndex = 7`. Example with all
defaults, map "A Hill Between" in Battle, seed 12345, V3 dynamic:

```
GO 115;5;10;0;1;0;1;<sha1>;Battle;7;0;2;12345;0;[2] A Hill Between;1;-1
```

(`115` = bits 0,1,4,5,6 set.) **UNVERIFIED at runtime**: confirm the count by capturing one
`GO` from a real host of the same package before relying on it.

"Spawn ini code" (`ApplySpawnIniCode`, GameSessionCheckBox.cs:165-177, GameSessionDropDown.cs:181-204)
is the translation of each option into `spawn.ini [Settings]` keys at game start; the host-bot
never writes spawn.ini (each client writes its own), so this table is only for interpreting
the bits. Map-code options (`CustomIniPath`, `DataWriteMode=MapCode`) are merged into each
client's `spawnmap.ini` instead.

### 2.6 `STARTV2` / `STARTV3` — game start (host → all)

`STARTV2` (CGL:1235-1251), V2 tunnels only:

```
STARTV2 <UniqueGameID>;<name1>;<tunnelAddr>:<port1>;<name2>;<tunnelAddr>:<port2>...
```

`port_i` come from the tunnel server (`CnCNetTunnel.GetPlayerPortInfo(Players.Count)`); every
name must be in the receiver's `Players` or it aborts (CGL:2297-2300); the receiver also
re-selects the V2 tunnel whose address matches its own entry (CGL:2265-2290).

`STARTV3` (CGL:1253-1257, V3NM:796-835):

```
STARTV3 <UniqueGameID>;<id1>;<name1>;<addr1>:<port1>;<id2>;<name2>;<addr2>:<port2>...
```

- `id_i` = `ReadUInt32LittleEndian(SHA1(UTF8("<name>:<channelName>")))` (V3NM:71-76),
  printed as unsigned decimal.
- `addr:port` = the static tunnel `Address:Port` in V3Static, `"0.0.0.0:0"` in V3Dynamic.
- Player `i` (0-based position in the message) gets local game port `48000 - i`
  (V3NM:767, 804) — the order defines in-game ids, so every receiver must see the same
  order.
- Receiver (CGL:2310-2366): `sender == hostName`; if already in game → error notice; if
  `Map == null` → sends `RETURN`; requires `parts.Length == Players.Count * 3 + 1` **exactly**
  and each name in `Players`; else "Failed to process the game start message" notice.

Both are followed by the host's own `StartGame` (section 5).

### 2.7 Map sharing (`MAPREQ`, `MAPFAIL`, `MAPOK`, `MAPSDISABLED`)

Trigger: a receiver of `GO` has no map with `(gameModeName, sha1)` (CGL:1955-1969):

- `mapOfficial == 1` → client sends `MAPFAIL <sha1>` and tells the user the host has a
  different game version (CGL:2159-2166). The bot must therefore never pick an official map
  the players may lack (i.e. use only maps from the package's `MPMaps.ini`).
- `mapOfficial == 0` → if the user has map sharing enabled (`EnableMapSharing`, default true)
  the client asks for confirmation and downloads
  `GET {CnCNetMapDBDownloadURL}/{game}/{sha1}.zip` (default
  `https://mapdb.cncnet.org/yr/<sha1>.zip`, MAPSHARER:362) into
  `Maps/Custom/<mapName>_<sha1>.map` (`GetMapFileName`, MAPSHARER:321-322; `mapName` is the
  GO `mapUntranslatedName` with Win32-invalid chars replaced); if sharing is disabled it sends
  `MAPSDISABLED` (CGL:2143-2157).
- Download failure (CGL:2767-2793): if the host already answered for this sha1 (`MAPOK` or
  `MAPFAIL` seen) → `MAPFAIL <sha1>`; otherwise → `MAPREQ <sha1>` ("please upload").
- Host on `MAPREQ <sha1>` (CGL:2905-2957): if `MapSharer.IsMapUploaded(sha1)` → reply
  `MAPOK <sha1>` (only if it is still the current map); else find the map; official → local
  notice only; custom → `MapSharer.UploadMap`: multipart POST to `CnCNetMapDBUploadURL`
  (default `https://mapdb.cncnet.org/upload`) with field `game=yr` and file field `file` =
  `<sha1>.zip` containing `<sha1>.map` (MAPSHARER:142-152, 211-219); success iff the response
  body contains `"Upload succeeded!"` (MAPSHARER:189). On success and map unchanged →
  `MAPOK <sha1>` (CGL:2891-2899); on failure and map unchanged → `MAPFAIL <sha1>` (CGL:2876-2889).
- Client on `MAPOK <sha1>` (host only): remembers the sha1 and, if it is still waiting for that
  map, re-downloads (CGL:2990-3002). Once the file lands in `Maps/Custom`, the file watcher
  adds it and the client selects it automatically (CGL:2805-2874); no CTCP is sent back —
  the host learns success only indirectly (the player can now ready up).
- `MAPFAIL` from the host = "host failed to upload"; from a player = "player cannot download"
  (CGL:2959-2988). `MAPSDISABLED` from a player = that player has sharing off (CGL:3004-3010).

Bot obligations: keep the `.map` bytes for every custom map it hosts, pre-upload them to the
map DB (or upload on first `MAPREQ`), answer `MAPREQ` with `MAPOK`, and treat a player with
`MAPFAIL`/`MAPSDISABLED` as unable to play on that map.

### 2.8 `FHSH` / `MM` / `CD` — file-hash check and cheat notices

- Each non-host sends `FHSH <hash>` (SYSTEM/10) right after it joined (CGL:437). `hash` =
  `FileHashCalculator.GetCompleteHash()` (FHC:145-224, 364-391): lowercase hex SHA1 of the
  ASCII concatenation of, in order, the SHA1s of `Resources/ClientDefinitions.ini`,
  `Resources/GameOptions.ini`, `Resources/clientdx.exe`, `clientxna.exe`, `clientogl.exe`,
  `Resources/BinariesNET8/Windows/clientdx.dll`, `.../XNA/clientxna.dll`,
  `.../OpenGL/clientogl.dll`, `.../UniversalGL/clientogl.dll`, the game executable
  (`Syringe.exe` per `GameExecutableNames`, skipped if `CalculateGameExeHash=False`), the
  launcher exe (empty in YR), `INI/MPMaps.ini`, `Resources/FHCConfig.ini`, then every
  "additional" file hash sorted case-insensitively by normalized relative path: the
  `[FilenameList]` of `FHCConfig.ini` (YR: `gamemd-spawn.exe, Syringe.exe, Ares.dll,
  CnCNet-Spawner.dll, Phobos.dll, cncnet.mix, ra2mode.mix, ares.mix,
  Compatibility/Unix/wine-game.sh`; this list replaces the built-in `ClientType.Ares` list),
  every file under `INI/Game Options/` recursively (not `INI/Map Code/` for YR), the coop
  difficulty INI folders, and the checked `[Translations] GameFile_*` targets of every
  translation folder. Missing files contribute an empty string. `.txt/.ini/.json/.xml` files
  are hashed after CRLF→LF normalisation and `Trim()` as UTF-8 without BOM (FHC:295-325).
- Host (CGL:2569-2585): marks `HashReceived = true` **regardless of the value**, and if
  `hash != gameFilesHash` sends `MM <nick>` (GAME_CHEATER/10) which makes every client print
  "Player X has different files compared to the game host" in red (sender must be host). This
  is advisory only; launch is blocked solely by `HashReceived == false` (`NVRFY`).
- `CD` (INSTANT/0) is sent by any client whose own files changed between joining and launch
  (CGL:2383-2388); receivers print a red notice (CGL:2674-2675). Nothing else happens.

A bot that is not a client cannot compute the reference hash from its own install; it should
either carry a reference hash captured from a trusted client of the same package version, or
skip `MM` entirely. It must still require an `FHSH` from every player before launching.

### 2.9 Notifications (host → all, exact-match, sender must be host)

Sent from the launch validation (section 3) and received by `HandleNotification`
(CGL:2441-2455):

| Command | Receiver text | When the host sends it |
|---|---|---|
| `AISPECS` | "AI players don't enjoy spectating matches…" | an AI has SideId == spectator |
| `GETREADY` | "The host wants to start the game but cannot because not all players are ready!" (+ sound for unready players) | some human (other than host) not ready |
| `INSFSPLRS` | "Unable to launch game: {map} cannot be played with fewer than {MinPlayers} players" | EnforceMinPlayers violated |
| `TMPLRS` | "… cannot be played with more than {MaxPlayers} players." | EnforceMaxPlayers violated |
| `CLRS` | "Multiple human players cannot share the same color." | duplicate ColorId > 0 |
| `SLOC` | "Multiple players cannot share the same starting location on this map." | duplicate start > 0 (EnforceMaxPlayers maps) |
| `LCKGME` | "The host needs to lock the game room before launching the game." | launch attempted while unlocked |
| `NVRFY <i>` | "Unable to launch game. Player {Players[i]} hasn't been verified." | `Players[i].HashReceived == false` |
| `INGM <i>` | "Unable to launch game. Player {Players[i]} is still playing the game you started previously." | `Players[i].IsInGame` |

`i` is the 0-based index in the **receiver's** `Players` list (which is the PO order), so the
bot must index into the roster exactly as it was broadcast.

### 2.10 `STRTD` / `RETURN` — in-game tracking

- Every client (host included) sends `STRTD` (SYSTEM/20) just before launching the game
  executable (CGL:2409) and `RETURN` (SYSTEM/20) when the game process exits or a start is
  aborted (CGL:2210). Receivers set/clear `IsInGame` for that nick (CGL:2533-2554); no sender
  check.
- Host uses `IsInGame` to block launches (`INGM`), to exclude in-game players from
  auto-ready (`PO` ready field), from `ClearReadyStatuses`, and from `RENEGALL` participants.
- After its own game exits the host client also sends `RETURN` and then re-rolls the seed and
  rebroadcasts (section 5).

### 2.11 `TNLPNG <ms>` — tunnel ping display

Each client sends its own ping to the current static tunnel (`"TNLPNG " + ms`, CGL:453) after
joining and whenever the tunnel is re-pinged; receivers store it on that player (`-1` →
unknown). Not sent and ignored in V3-dynamic mode. Cosmetic; a bot may omit it.

### 2.12 `DR` — dice roll

`DR <sides>,<r1>,<r2>,...` (CHAT/0, CGL:2595-2600) from `/roll`. Receivers validate
`2 ≤ parts ≤ 11`, `1 ≤ sides ≤ 100`, each `1 ≤ r ≤ sides` (MGL:580-606) and print
"{sender} rolled {n}d{sides} and got …".

### 2.13 Tunnel-mode / tunnel-server messages

- `CHTNL <address>:<port>` (SYSTEM/10) — host announces a new static tunnel (manual selection
  CGL:621-639, auto-select CGL:2706-2723). Receivers (CGL:2677-2704): sender must be host; the
  tunnel must exist in their tunnel list, else they enter `tunnelErrorMode` and cannot ready up.
- The tunnel **mode** itself is carried only in `GO` field `partIndex+9` (CGL:2065-2069).
  Switching to/from dynamic mode resets pings and negotiation state (CGL:2092-2141).
- `RENEGALL <name1>,<name2>,...` (SYSTEM/10) — host asks the listed (non-in-game) players to
  restart their pairwise negotiations (CGL:1718-1757, receiver 1662-1700).
- `NEGRPT ...` (GAME_NEGOTIATION/10) — pairwise negotiation report; see the tunnel document.

### 2.14 Chat

`Channel.SendChatMessage` (CH:261-269):

```
PRIVMSG <#channel> :\x03<NN><text>        NN = IrcColorId formatted "D2"
```

Colour ids (CnCNetManager.cs:63-78): 0/1 default, 2 Light Blue, 3 Green, 4 Dark Red, 5 Red,
6 Purple, 7 Orange, 8 Yellow, 9 Lime Green, 10 Turquoise, 11 Sky Blue, 12 Blue, 13 Pink,
14 Metallic, 15 Gray (0, 1 and 15 are not user-selectable). Input is capped at 150 characters
(MGL:178). Messages from ignored users are suppressed client-side (CGL:1121-1136).

Private CTCP `INVITE` (`ProgramConstants.GAME_INVITE_CTCP_COMMAND`, handled in LOBBY:58) is
sent user-to-user, not in the game channel; out of scope.

---

## 3. Join sequence (host side, `Channel_UserAdded`, CGL:1006-1056)

Triggered by the IRC `JOIN` of another nick (CONN:697-704 → `Channel.AddUser` → `UserAdded`).
Note `OnUserListReceived` (NAMES reply, CH:132-154) adds users **without** raising `UserAdded`,
so only users joining after the host are turned into players this way (the host itself joined
first, so this is always true for a host).

1. If `Players.Count >= 8` or `Players.Count >= playerLimit`: `KICK <channel> <nick>`
   (INSTANT/8) and stop.
2. `Players.Add(new PlayerInfo(nick))` — SideId 0, ColorId 0, StartingLocation 0, TeamId 0,
   `Ready=false`, `AutoReady=false`, `HashReceived=false`, `IsInGame=false`.
3. While `Players.Count + AIPlayers.Count > 8` remove the last AI.
4. `ChangeMap(GameModeMap)` (GLB:2579-2771): re-applies the map/game-mode forced options,
   drops the new player's invalid start/team values, forces `TeamId=1` on coop maps, clears
   ready statuses, and ends with `OnGameOptionChanged()` → **`GO` broadcast**.
5. `BroadcastPlayerOptions()` → **`PO`**.
6. `BroadcastPlayerExtraOptions()` → **`PEO`**.
7. If `Players.Count >= playerLimit`: notice + `LockGame()` → `MODE +i`, accelerate `GAME`.
8. `StartNegotiationForPlayerName(nick)` (V3-dynamic only; tunnel document).

No explicit delays; the three CTCPs are queued and go out one per `SendSleep` (2.5 s) in
priority order (GO and PO have priority 11, PEO 11 with replace, so order is GO, PO, PEO as
queued). Meanwhile the joiner sends `FHSH <hash>` and (static tunnels) `TNLPNG <ms>` to the
channel (CGL:437, 453); the host answers `FHSH` as in 2.8.

A joiner that sees the host nick missing from the NAMES reply leaves immediately
(CGL:989-1002); a joiner whose `JOIN` fails gets 471 (full), 473 (invite-only = locked),
474 (banned) or 475 (bad key) and the lobby shows the matching error (CONN:650-660).

On leave/quit/kick of a player (CGL:937-987, 1058-1086): remove from `Players`, `PO`
rebroadcast (leave/quit path), unlock if the room was auto-locked and no game is running.

---

## 4. Launch validation (host, `BtnLaunchGame_LeftClick` MGL:784-924 → `HostLaunchGame` CGL:1140-1222)

Checks run in this order; the first failure sends the listed CTCP (all GAME_NOTIFICATION/0
except `GETREADY` = GAME_GET_READY/0) plus a local notice, and aborts:

1. Non-host → this is the ready button (`R`). Host continues.
2. `!Locked` → `LCKGME` (CGL:2509-2515).
3. `GetTeamMappingsError()` non-empty → local notice only (no CTCP).
4. Two humans with the same `ColorId > 0` → `CLRS`.
5. Any AI with `SideId == ddPlayerSides[0].Items.Count - 1` (spectator, 14) → `AISPECS`.
6. If `GameModeMap.EnforceMaxPlayers`: any two of humans ∪ AIs with the same
   `StartingLocation > 0` → `SLOC`.
7. `totalPlayerCount = humans with SideId < 14 (non-spectators) + AIPlayers.Count`;
   `EnforceMinPlayers && total < MinPlayers` → `INSFSPLRS`;
   `EnforceMaxPlayers && total > MaxPlayers` → `TMPLRS`
   (`GameModeMap` properties: GameModeMap.cs:61-83; Enforce* default to `IsCoop` when unset,
   so most YR maps enforce nothing unless `MPMaps.ini` says `EnforceMaxPlayers=True`).
8. For every player except the host's own nick, in `Players` order: `!HashReceived` →
   `NVRFY <idx>`; `IsInGame` → `INGM <idx>`; `!Ready` → `GETREADY`. Spectators must be ready
   too (the spectator exemption is commented out, MGL:900-918).
9. `HostLaunchGame` (CGL:1140-1222):
   - V3-dynamic: `LaunchConnectivityCheckInProgress` → "Still verifying player connections…";
     not all pair negotiations successful → red/yellow notices listing failed/incomplete pairs,
     no CTCP; abort.
   - If `Players.Count > 1`:
     - V2 tunnel (`CurrentTunnel.Version == 2`): request `Players.Count` ports from the tunnel;
       fewer → error notice, abort; else `STARTV2`.
     - else reserve the local game port (failure → notice, abort);
       V3-dynamic → asynchronous connectivity check then `STARTV3` (`FinishV3DynamicLaunch`);
       V3-static → `STARTV3`.
   - With exactly one player (host alone) no START message is sent; the host just launches.
10. `StartGame()` (section 5).

Nothing in the launch path checks `MinPlayers` when `EnforceMinPlayers` is false, nor the
room's `playerLimit` (that is enforced at join time).

---

## 5. Seeds, ids and the host's start/return flow

### 5.1 `RandomSeed`

`RandomSeed = random.Next()` when the room is created (CGL:327) and again in `ResetGameState`
after every game (CGL:2215). Carried in `GO` (field `partIndex+6`); every client seeds
`new Random(RandomSeed)` to randomise sides/colours/starts identically (GLB:2263) and writes
`[Settings] Seed=` into spawn.ini (GLB:1775). Changing it requires a new `GO`.

### 5.2 `UniqueGameID`

`MGL.GenerateGameID` (MGL:311-327): for `i = 0..19`,
`UniqueGameID = int.Parse(i + Day + Month + Hour + Minute)` (no zero padding, local time) until
no match with that id exists in the local statistics DB. Generated in `Refresh(isHost=true)`
(MGL:660, i.e. at room creation) and again in `GameProcessExited` for the host (MGL:302-303).
Carried only in `STARTV2`/`STARTV3`; clients write it to spawn.ini `[Settings] GameID`
(CGL:2432) and into their match statistics. Any non-negative int that is unlikely to repeat
is acceptable to receivers (`IntFromString(parts[0], -1) < 0` rejects, CGL:2246-2248, 2338-2344).

### 5.3 Host `StartGame` (CGL:2376-2411 → MGL:272-281 → GLB:2261-2273)

1. Notice "Starting game…"; recompute the file hash, send `CD` if it changed.
2. Stop the inactivity checker.
3. V3 (static or dynamic): `FindLocalPlayer()` must exist and the local game bridge must start
   (`_negotiator.StartGameBridge()`), else abort with a notice.
4. Send `STRTD` (SYSTEM/20).
5. Write `spawn.ini` (GLB:1725-1902) incl. `[Settings] Name, Scenario=spawnmap.ini,
   UIGameMode, UIMapName, MapID (official maps), PlayerCount, Side, IsSpectator, Color,
   AIPlayers, Seed, CoachMode, AutoSurrender, CoopDifficultyLevel, FrameSendRate, MaxAhead
   (if >0), Protocol, GameID, Host=True (host) / False (others), Port, [Tunnel] Ip/Port,
   MapSHA1, BroadcastedGameOptionValues`, `Other1..N` sections for every other human with
   `Ip=0.0.0.0`/`Port`, `[HouseHandicaps]/[HouseCountries]/[HouseColors]` for AIs,
   `[SpawnLocations]`, alliances; write `spawnmap.ini`; launch the game; the IRC connection
   stays up and the lobby keeps processing CTCPs.

### 5.4 After the host's game exits (`GameProcessExited` → `ResetGameState`, CGL:2204-2226; MGL:285-309; GLB:2277-2297)

1. Stop the game bridge; send `RETURN`; mark self not in game.
2. Host: `RandomSeed = random.Next()`; `OnGameOptionChanged()` → `GO`; `ClearReadyStatuses()`;
   `PO`; `PEO`; restart inactivity check; `UnlockGame` if `Players.Count < playerLimit`.
3. Host: `GenerateGameID()` (new `UniqueGameID`), refresh map ranks.
4. Non-host with auto-ready checked: re-sends `R 2` after its own game exits (MGL:305-308).

The `AutoReady` flag is purely a player preference carried in `R` (value 2) and echoed in
`PO` (value 2) so other clients can show it; the host must keep `Ready` set across option
changes for auto-ready players (that is what `ClearReadyStatuses` does) but must clear it when
the map becomes invalid (`resetAutoReady`) or when the player is in game.

---

## 6. Assumptions that the host is a player — and what a non-playing bot must do

Places that assume the local user (host) is `Players[0]` / `FindLocalPlayer()`:

| Where | Assumption | Impact for a bot that is **not** in the roster |
|---|---|---|
| CGL:1040-1043 | own JOIN echo → `Players[0].Ready = true` | n/a |
| GLB:2380-2387 `ClearReadyStatuses` | skips index 0 | bot must clear readiness of **all** players itself; on clients index 0 is merely never auto-cleared (cosmetic) |
| MGL:1050-1060 `CopyPlayerDataToUI` | index 0 shows the lock state instead of ready | cosmetic on clients: the first real player's indicator shows lock state |
| MGL:883-923 launch loop | skips `player.Name == PLAYERNAME` | bot checks every player |
| GLB:1760-1777 `WriteSpawnIni` | `myIndex = Players.FindIndex(PLAYERNAME)`; `PlayerCount = Players.Count`; one `Other<n>` section per other human | **clients would write the bot as an in-game player** if it appears in `PO` → the bot must not list itself in `PO` |
| CGL:2237-2300 / 2310-2366 START handlers | every listed name must be in `Players`; STARTV3 needs exactly `3·Players.Count + 1` parts | bot must build START messages from the human roster only (no own entry); `Players.Count` on clients equals the number of humans in the last `PO` |
| V3NM:796-835 `GenerateV3StartPayload` | iterates `host.Players` | same; ids/ports by message position |
| CGL:2394-2407 `StartGame` V3 branch | `FindLocalPlayer()` must exist, local bridge started | bot does not run the game; skip |
| MGL:296-297 `GameProcessExited` | `Players.Find(PLAYERNAME).IsInGame = false` | n/a (bot has no game process) |
| CGL:453-460 `UpdatePing` | sets own ping | optional `TNLPNG`; harmless |
| CGL:1480 `PO` builder, CGL:3115 `GAME` builder | include the host in the roster | bot omits itself from `PO`; it may omit itself from the `GAME` player list too (field 6) — UNVERIFIED whether any client UI dislikes an empty list |
| V3 negotiation (`IV3NegotiationHost.Players`) | pairs are formed among `Players` | bot absent from the roster ⇒ no pair involves the bot, which is what we want; the bot must evaluate "all negotiations successful" over the players only |
| yrpp-spawner `Spawner.cpp:420`, `KickAgreement.cpp:1025-1032` | the client with `Host=True` announces itself as game master | with no `Host=True` client, `SessionClass_Am_I_Master` falls back to "first non-defeated human house" (`Ext/Session/Body.cpp:160-185`), so the game still has a master. **UNVERIFIED for the stock CnCNet spawner** used by public players. |

Minimum the bot must still emit as host: `MODE +klnNs`, `TOPIC`, periodic `GAME`, `GO`, `PO`,
`PEO` (on join, on every change, after game), `MODE +i/-i`, the notification CTCPs from
section 4, `STARTV2`/`STARTV3`, `MAPOK`/`MAPFAIL` replies, optionally `MM`, `CHTNL`,
`RENEGALL`, `GSETTINGS`. It should not send `STRTD`/`RETURN` for itself (they would only
touch a roster entry the clients do not have).

Because clients rebuild their roster from `PO` and ignore names not in the channel, the bot
must use the players' exact IRC nicks and keep its own nick equal to the `GAME` sender.

---

## 7. Map data the host needs

### 7.1 Identity

- `Map.SHA1` = `Utilities.CalculateSHA1ForFile(CompleteFilePath)` = SHA1 over the **raw bytes
  of the whole `.map` file**, lowercase hex, 40 chars (Map.cs:198-201, Utilities.cs:19-40,
  `BytesToHexString(capitalize:false)`). No normalisation. `CompleteFilePath =
  <GamePath>/<BaseFilePath>.<MapFileExtension>` (`map`).
- `Map.Official` = loaded from `MPMaps.ini` (constructed with `isCustomMap=false`); custom =
  anything from `Maps/Custom/*.map` (Map.cs:49-62). Official maps are never uploaded/shared.
- `GO` carries `GameMode.Name` (the internal section name) and `Map.UntranslatedName`
  (`Description=` for official, `[Basic] Name=` for custom). Receivers match on
  `(GameMode.Name, SHA1)` — the map must be listed under that game mode on the receiving client
  (`GameModeMaps`), so the host must pick a `(mode, map)` pair that exists in the package.

### 7.2 Official list (`MapLoader`, MapLoader.cs:140-168, 418-501)

`INI/MPMaps.ini` (path from `ClientDefinitions.ini MPMapsPath`):

- `[GameModes] 0=Battle 1=YR Ladder 2=RA2 Ladder 3=RA2 Pro 2v2 4=Free For All 5=Cooperative
  6=Naval War 7=Unholy Alliance 8=Megawealth 9=Meat Grinder 10=Team Alliance 11=Land Rush
  12=Mod Maps 13=Standard 14=Blitz 15=Survival 16=Oil In Centre` — each becomes a `GameMode`
  (section `[<Name>]`, GameMode.cs:100-125): `UIName` (defaults to Name), `MapCodeIniName`
  (defaults `<Name>.ini` in `INI/Map Code/`), `ForcedOptions=<section>` (check box/drop-down
  values forced while the mode is selected; ints are drop-down indices, everything else a
  boolean), `ForcedSpawnIniOptions=<section>` (default `<Name>ForcedSpawnIniOptions`, e.g.
  `GameMode=1`), `DisallowedPlayerSides`, `DisallowedHumanPlayerSides`,
  `DisallowedComputerPlayerSides`, `MinPlayersOverride`, `MaxPlayersOverride`,
  `UseDifficultyDropDown`, plus the common keys below. Game modes with zero maps are dropped.
- `[GameModeAliases]` (absent in YR): `mode=alias1,alias2`.
- `[MultiMaps] <path>=<path>` — one key per official map, value = base path without extension
  relative to the game dir (e.g. `Maps\Yuri's Revenge\hillbtwn`). The map's section is
  `[<path>]` (`BaseSection=` may chain). The YR file has 573 map sections.
- Per-map keys (`Map.InitializeFromMpMapsINI`, Map.cs:229-367; common keys
  GameModeMapBase.cs:96-146):
  `Description` (name), `Author`, `GameModes=Battle,Free For All,...`, `PreviewImage`,
  `Briefing` (`@` = newline), `MinPlayers`/`MaxPlayers` (aliases `ClientMinPlayer`,
  `ClientMaxPlayer`, `MinPlayer`, `MaxPlayer`), `EnforceMaxPlayers`, `EnforceMinPlayers`,
  `AllowedStartingLocations=0,1,...` (0-based in the file, 1-based in code, count must equal
  MaxPlayers), `IsCoopMission`, `MultiplayerOnly`, `HumanPlayersOnly`,
  `ForceRandomStartLocations`, `ForceNoTeams`, `CoopDifficultyLevel`, `Credits`, `UnitCount`,
  `NeutralColor`, `SpecialColor`, `Bases`, `ExtraTextureN`, `Size`, `LocalSize`,
  `WaypointN` (N = 0..7, stops at the first missing), `TeamStartMappingN` + `...Name`,
  `ForcedOptions=<sections>` (map-level forced options, e.g. `razer_survival_options`),
  `ForcedSpawnIniOptions=<sections>`, `ExtraIniName`. Example:

  ```
  [Maps\Yuri's Revenge\hillbtwn]
  Description=[2] A Hill Between
  Author=Westwood Studios
  GameModes=Battle,Free For All,Unholy Alliance
  Waypoint0=102063
  Waypoint1=27066
  MinPlayers=1
  MaxPlayers=2
  EnforceMaxPlayers=True
  ```
- Effective per-(mode,map) values (`GameModeMap`, GameModeMap.cs:42-83): `MaxPlayers =
  GameMode.MaxPlayersOverride ?? Map.MaxPlayers ?? GameMode.MaxPlayers ?? 0`; `MinPlayers`
  likewise; `EnforceMax/MinPlayers = Map ?? GameMode ?? IsCoop`; `AllowedStartingLocations =
  Map ?? GameMode ?? 1..MaxPlayers`; `IsCoop`, `CoopInfo`, `ForceNoTeams`,
  `ForceRandomStartLocations`, `HumanPlayersOnly` = `Map ?? GameMode ?? false`.

### 7.3 Custom maps

`Maps/Custom/*.map` (`Map.InitializeFromCustomMap`, Map.cs:450-556): `[Basic] Name`, `Author`,
`GameModes` (or `GameMode`, default `Default`, first letter upper-cased), `Briefing`, the same
common keys, `[Map] Size/LocalSize`, `[Waypoints] 0..7`, `[ForcedOptions]`,
`[ForcedSpawnIniOptions]`. A custom map is listed only under game modes contained in
`AllowedCustomGameModes` (YR: `Standard,Custom Map,Teamgame,Duel,Airwar,Nukewar,Navalwar`;
MapLoader.cs:756). Downloaded maps are stored as `<name>_<sha1>.map`; the file name is
irrelevant to the protocol (only SHA1 and GameMode matter).

---

## 8. Chat-box `/commands` (MGL:39-57, CGL:116-135)

Typed by the local user; `/<cmd>` is case-insensitive, parameters follow the first space
(MGL:348-396). None of these go over the wire by themselves; their effects do.

| Command | Host only | Effect (relevant wire traffic) |
|---|---|---|
| `/HIDEMAPS`, `/SHOWMAPS` | yes | UI only |
| `/FRAMESENDRATE <n>` | yes | `FrameSendRate = n`, notice, `GO`, clear ready |
| `/MAXAHEAD <n>` | yes | `MaxAhead = n`, `GO`, clear ready |
| `/PROTOCOLVERSION <0|2>` | yes | only 0 or 2 accepted, `GO`, clear ready |
| `/LOADMAP <file>` | yes | loads `Maps/Custom/<file>.map` into the list (no wire) |
| `/RANDOMSTARTS <bool>` | yes | `RemoveStartingLocations`, `GO`, clear ready (TS-only feature; on YR it only strips waypoints 0-7 when the map does not enforce max players) |
| `/ROLL [NdS]` | no | `DR S,r1..rN` (N 1..10, S 2..100, default 1d6) |
| `/SAVEOPTIONS <name>`, `/LOADOPTIONS <name>` | no / yes | local presets; loading triggers `GO` |
| `/TUNNELINFO` | no | local notice |
| `/CHANGETUNNEL` | yes | tunnel window → `CHTNL` + `GO` (mode) + clear ready |
| `/DOWNLOADMAP <sha1> [name]` | no | downloads from the map DB by id (no wire) |
| `/NEGSTATUS`, `/NS` | no | toggles the negotiation panel |
| `/RENEGOTIATE` | yes | `RENEGALL <names>` (dynamic mode only) |

Non-hosts typing a host-only command get "/X is for game hosts only."; unknown commands open
the help box.

---

## 9. Compact host-side message cheat-sheet

```
MODE #chan +klnNs <pw> <limit>              once, after own JOIN
TOPIC #chan :B;yr                           once
GAME <15 fields>                            to #cncnet-yr-games every 30 s (<35 s)
GO <ints…>;<official>;<sha1>;<mode>;<fsr>;<maxahead>;<proto>;<seed>;<rmstarts>;<mapname>;<tunnelmode>;<diff>
PO <name>;<team|start<<8|color<<16|side<<24>;<0|1|2>;…[<ailevel>;<packed>;]…
PEO <5 bits>;<mappings>
GSETTINGS <room>;<max>;<skill>;<custompw>
MODE #chan +i / -i                          lock / unlock
LCKGME | CLRS | AISPECS | SLOC | INSFSPLRS | TMPLRS | NVRFY <i> | INGM <i> | GETREADY
MM <nick>                                   hash mismatch notice
MAPOK <sha1> | MAPFAIL <sha1>               map sharing replies
CHTNL <ip>:<port> | RENEGALL <n1,n2,…>      tunnel handling
STARTV2 <gid>;<n>;<ip>:<port>;…             V2 launch
STARTV3 <gid>;<id>;<n>;<ip>:<port>;…        V3 launch
KICK #chan <nick> | MODE #chan +b *!*@<host>
PRIVMSG #chan :\x03NN<text>                 chat
```

Incoming to act on: `OR`, `R`, `FHSH`, `MAPREQ`, `MAPFAIL`, `MAPSDISABLED`, `STRTD`, `RETURN`,
`TNLPNG`, `DR`, `CD`, `NEGRPT`, IRC `JOIN`/`PART`/`QUIT`/`KICK`/`NICK`/`353`.

---

## 10. Unknowns / UNVERIFIED

1. Exact `CheckBoxes.Count`/`DropDowns.Count` (18/6) for the YR package was derived from INI
   key order, not captured from a running client. Capture one real `GO` (expect 18 fields).
2. The YR package references `LocalGameLobbyCheckBox`, which the `develop` source lacks; the
   package is evidently used with the user's `live-test-9.3.x` client, whose `GO`/`GAME`
   messages **omit the trailing difficulty field** and add extra negotiation commands
   (`TunnelRenegotiate`, `TunnelFailed`). Decide which client population the bot targets.
3. Whether the stock CnCNet YR spawner (not `yrpp-spawner`) tolerates a game where no client
   has `Host=True` (bot not a player). In `yrpp-spawner` the master falls back to the first
   human house.
4. Whether any client code chokes on a `GAME` broadcast with an empty player list (field 6)
   or on a `PO` whose first entry is not the host nick (only cosmetic effects were found).
5. `PEO` and `GSETTINGS` have no sender check; any player could spoof them to other players.
   Nothing the bot can do except notice it.
6. Whether a `PO` is rebroadcast after the host kicks a player (the kick path only updates
   UI; the subsequent PART/QUIT should trigger it, not verified).
7. Reference file hash for `MM`: the bot cannot compute the clients' hash without a full
   install; it must be captured from a trusted client per package version.
8. `GAME_VERSION` string the players run (`ProgramConstants.GAME_VERSION`, set from the
   updater's version file) must be known for field 1 of `GAME`.
9. Behaviour of the clients when the host sends `STARTV3` with V3-static addresses for a tunnel
   the client does not have in its list (`ApplyV3StartEntry` returns false → start fails) —
   the bot must only use tunnels from the public list.
