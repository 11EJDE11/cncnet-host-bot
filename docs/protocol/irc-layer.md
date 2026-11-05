# CnCNet client IRC layer – wire reference for a headless YR host bot

Source of truth: `C:\Users\QWE\source\repos\11EJDE11\xna-cncnet-client` at commit `64cfd548`
(`git describe` = `2.11.0.0-alpha.2-605-g64cfd548`). All `file:line` references below are relative to that
checkout. Installed-client values (LocalGame, MaxNameLength, server list, version) were read from
`C:\Users\QWE\Desktop\Red Alert 2x\Game\Resources\{ClientDefinitions,NetworkDefinitions}.ini` and `...\Game\version`.

Notation: `\x01` = byte 0x01, `\x03` = 0x03, `CRLF` = `\r\n`. `{x}` = substituted value. Everything the client sends
is `message + "\r\n"` encoded UTF-8 (`Connection.cs:106`, `:980`). Everything in the wire examples is literal
unless in `{}`.

---

## 1. Servers, connect order, timeouts, keepalive, nick collision

### 1.1 Server list

Source: `ClientConfiguration.IRCServers` (`ClientCore/ClientConfiguration.cs:558`, `:850-860`) reads section
`[IRCServers]` of `Resources/NetworkDefinitions.ini`; every non-empty value is one server string
`host|name|port,port,...` (`DXMainClient/Online/Server.cs:21-35`). If the INI has none, hard-coded fallback
(`Connection.cs:56-58`):

```
irc.gamesurge.net|GameSurge|6667,6660,6666,6668,6669
```

Installed YR client (`Red Alert 2x\Game\Resources\NetworkDefinitions.ini`, also identical in the 9.3.1 install):

```
1=irc.gamesurge.net|GameSurge|6667
2=LAN-Team.DE.EU.GameSurge.net|GameSurge Nuremberg, Germany|6660,6666,6667,6668,6669
3=Stockholm.SE.EU.GameSurge.net|GameSurge Stockholm, Sweden|6666,6669,7000,8080
4=NuclearFallout.WA.US.GameSurge.net|GameSurge Seattle, WA|6667,5960
5=Prothid.NY.US.GameSurge.Net|GameSurge NYC, NY|5960,6660,6666,6667,6668,6669,6697
6=192.223.27.109|GameSurge IP 192.223.27.109|5960,6660,6666,6667,6668,6669
7=162.248.94.123|GameSurge IP 162.248.94.123|6667,5960
8=128.140.107.226|GameSurge IP 128.140.107.226|6660,6666,6667,6668,6669
9=188.240.145.60|GameSurge IP 188.240.145.60|6660,6666,6667,6668,6669
```

- **No TLS anywhere.** Plain `TcpClient(AddressFamily.InterNetwork)` (`Connection.cs:179`), IPv4 only (DNS results
  filtered to `InterNetwork`, `:357-358`). Port 6697 appears in the list but is used as plain TCP like any other.
- No `PASS`, no `CAP`, no SASL, no services login anywhere in the code.

### 1.2 Connect order (`Connection.cs:167-222`, `:337-501`)

1. Resolve every host (A records only), group by IP (duplicate hosts merged, ports unioned) (`:340-406`).
2. Skip IPs in `failedServerIPs` (IPs that accepted and then dropped a connection) (`:429-433`).
3. ICMP ping each IP with 400 ms timeout (`MAXIMUM_LATENCY`, `:26`, `:444`); non-responders get `long.MaxValue`.
4. Sort ascending by RTT (`:477-479`); iterate servers, and for each server iterate its ports in listed order
   (`:171-175`).
5. Per port: `BeginConnect` + wait **3 s** (`:180-181`); on timeout try next port; on success start the send
   queue thread and read loop (`:199-207`).
6. Only after *all* servers/ports fail: `failedServerIPs.Clear()` and `OnConnectAttemptFailed` (`:217-221`).

### 1.3 Registration timing

`Register()` is called immediately after the socket connects, before any server banner is read
(`Connection.cs:229`). Also re-sent on numeric `451` (not registered) (`:647-649`) – guarded by
`welcomeMessageReceived` (`:936`), which is set on `001` (`:576-580`).

### 1.4 Read loop, silence timeout, keepalive (`Connection.cs:224-329`)

- Socket `ReadTimeout = 1000` ms (`:204`); buffer 1024 bytes (`:27`), decoded as UTF-8 **per chunk** (`:292`) –
  a multi-byte char split across chunks is corrupted (client quirk, not a server rule).
- Every read timeout (IOException) or 0-byte read increments `errorTimes`; `> MAX_ERROR_COUNT` (30, `:23`) ⇒
  disconnect, IP added to `failedServerIPs` (`:252-287`). I.e. ~30 s with no bytes at all ⇒ connection declared
  lost. Any received byte resets the counter (`:289`).
- Keepalive timer `AutoPing` (`:231`): first fire 30 s after registration, then every 120 s; **after every received
  message it is re-armed to fire 30 s later and every 30 s** (`:296`). Payload (`:926-929`):

  ```
  PING LAG{rnd}        rnd = Random.Next(100000, 999999)
  ```
  sent directly (bypasses queue).
- Server `PING` ⇒ queued `PONG {param0}` (or bare `PONG` if no param), type `SYSTEM_MESSAGE`, priority 5000
  (`:757-768`).
- Reconnect: when the connection was cut unintentionally, wait 4000 ms (`RECONNECT_WAIT_DELAY`, `:24`) and retry up
  to 8 times (`MAX_RECONNECT_COUNT`, `:22`, `:305-328`); counter reset on `001` (`:580`).
- Intentional disconnect: sends `QUIT` (no reason) then closes socket (`:503-510`).

### 1.5 Nick in use – numeric 433 (`Connection.cs:642-646`, `CnCNetManager.cs:904-936`)

On `433`: if `len(nick) < MaxNameLength` append `_`; else replace the **last non-underscore** char with `_`; if the
name is all underscores give up and disconnect. Then send `NICK {newnick}` (`Connection.cs:951-954`). `432`
(erroneous nick) is only displayed (`:591`). `MaxNameLength` = `[Settings] MaxNameLength` in
`ClientDefinitions.ini`, default 16 (`ClientConfiguration.cs:275`); installed YR clients have **12** (9.3.1 install)
and **15** (9.3.5 install).

### 1.6 Other numerics the client reacts to (`Connection.cs:572-663`)

| numeric | action |
|---|---|
| 001 | welcome; triggers channel joins (§3.4) |
| 301 | AWAY reply (`params[1]`=nick, `[2]`=reason) |
| 332 | topic: `OnChannelTopicReceived(params[1], params[2])`, formatting stripped |
| 353 | NAMES: `params[2]`=channel, `params[3]` split on space; `@`/`+` prefixes parsed (`CnCNetManager.cs:766-775`) |
| 352 | WHO reply: ident=`params[2]`, host=`params[3]`, nick=`params[5]`, extra=`params[7]` (trailing `"<hops> <realname>"`) |
| 311 | WHOIS: ident=`params[2]`, host=`params[3]`, nick=`params[1]` |
| 433 | nick in use (above) |
| 439 | "target change too fast": `OnTargetChangeTooFast(params[1], params[2])` – aborts a game-join attempt (`CnCNetLobby.cs:999-1003`) |
| 451 | re-register |
| 471 | channel full (`params[1]`=channel) |
| 473 | invite-only (= "game locked") |
| 474 | banned from channel |
| 475 | bad key ("incorrect password") |
| 002,003,251,252,254,255,265,266,401,403,404,432,461,465 | displayed only |

---

## 2. Registration lines, ident, realname, hostmask

### 2.1 Exact lines (`Connection.cs:934-949`)

```
USER {LocalGame}.{systemId} 0 * :{GAME_VERSION} {LocalGame} CnCNet
NICK {PLAYERNAME}
```

Format strings: `"USER {0} 0 * :{1}"` with `{0} = defaultGame + "." + systemId`, `{1} = realname`
(`:945-946`); `realname = ProgramConstants.GAME_VERSION + " " + defaultGame + " CnCNet"` (`:943`);
`"NICK " + ProgramConstants.PLAYERNAME` (`:948`).

- `LocalGame` = `[Settings] LocalGame` of `ClientDefinitions.ini` (`ClientConfiguration.cs:225`, default `DTA`).
  Installed YR client: **`YR`** (exact case is used as-is in the ident; comparisons elsewhere are case-insensitive).
- `GAME_VERSION` = `[DTA] Version` of the game folder's `version` file (`ClientUpdater/Updater.cs:263-264`), set in
  `LoadingScreen.cs:111`; `"N/A"` if `ModMode=true` or no version file. Installed: `9.3.5` (another install: `9.3.1`).
  Example realname: `9.3.5 YR CnCNet`.

Concrete example: `USER YR.1a2b3c 0 * :9.3.5 YR CnCNet`.

### 2.2 systemId (`Connection.cs:124-132`, `Startup.cs:312-377`)

```
maxLength = ID_LENGTH(9) - (LocalGame.Length + 1)        // YR ⇒ 9 - 3 = 6
systemId  = CalculateSHA1ForString(id).Substring(0, maxLength)
```

`CalculateSHA1ForString` = SHA-1 over `Encoding.UTF8.GetBytes(str)`, returned as **lowercase** hex, no separators
(`Rampastring.XNAUI/Rampastring.Tools/Utilities.cs:47-62`, `:70`; verified empirically:
`CalculateSHA1ForString("test") == "a94a8fe5ccb19ba61c4c0873d391e987982fbbd3"`). So the ident is
`YR.` + first 6 lowercase hex chars of SHA-1 (9 chars total, matching `ID_LENGTH`).

`id` on Windows (`Startup.cs:321-341`): `cpuid + mbid + sid` where cpuid = WMI `Win32_Processor.ProcessorID`,
mbid = `Win32_BaseBoard.SerialNumber`, sid = machine `AccountDomainSid`; also stored in registry
`HKCU\SOFTWARE\{InstallationPathRegKey}\Ident`. On failure: a random `int` persisted in the same registry value
(`:343-359`). Linux: `/var/lib/dbus/machine-id` (`:367-371`). For a bot any stable string works; the server never
validates it.

### 2.3 How the client learns its own / others' hostmask

- From the `JOIN` echo prefix `nick!ident@host` (`Connection.cs:698-706`): `userName = prefix[0..'!']`,
  `ident = prefix['!'+1..'@']`, `host = prefix['@'+1..]`. Stored as `IRCUser(name, ident, host)` only if the nick
  is **not already** in the global `UserList` (`CnCNetManager.cs:594-616`); if already known (e.g. seen via NAMES in a
  chat channel) the existing object is cloned and **ident/host are not updated**.
- From `352` (WHO) / `311` (WHOIS) replies (`CnCNetManager.cs:857-887`): sets `Ident`, `Hostname`, and `GameID`
  (parsed from the realname: `extraInfo.Split(' ')[2]` = the game id in `"<hops> <version> <game> CnCNet"`).
- `WHO #channel` is sent by `Channel.RequestUserInfo()` (`Channel.cs:370-373`) **only when
  `ClientGameType != YR`** (`Channel.cs:125-129`) – for YR the client never sends `WHO`. `WHOIS {nick}` is sent when the
  user context-menu is opened (`GlobalContextMenu.cs:227`, `CnCNetManager.cs:178-181`).
- The game-id of another user is also derived from their ident: `ident.Split('@')[0].Split('.')[0]` with `~`
  stripped, upper-cased and matched against `InternalName.ToUpper()` (`CnCNetManager.cs:606-613`). I.e. GameSurge
  presents the ident as `~YR.xxxxxx` (no identd ⇒ `~` prefix) and the client tolerates it.
- Ignore list is keyed by the full ident string as received (with `~`) (`CnCNetUserData.cs:210`,
  `CnCNetLobby.cs:1470`).

### 2.4 Post-login commands

Nothing is sent between `NICK` and `001`. On `001` the channel joins of §3.4 are queued. The client sets
`AWAY :In-game` when the game executable starts and `AWAY` (clear) when it exits (`CnCNetLobby.cs:685-695`), both
`SYSTEM_MESSAGE` priority 0. No `MODE {nick} +x`, no user-mode commands at all.

---

## 3. Channels

### 3.1 Names (`GameCollection.cs:131-139`, `:156-162`)

| purpose | name | key | persistent | "chat channel" |
|---|---|---|---|---|
| YR main chat | `#cncnet-yr` | `ra1-derp` (`CnCNetLobby.cs:531-532`, every game's chat channel uses this literal key) | yes | yes |
| YR game broadcast | `#cncnet-yr-games` | none (`CnCNetLobby.cs:544-546`) | yes | no |
| General chat | `#cncnet` | `ra1-derp` | yes | yes |
| Game room | `#cncnet-yr-game{N}` (§3.2) | §3.3 | no | yes |

All channel names are lower-cased on construction (`Channel.cs:51`) and looked up case-insensitively
(`CnCNetManager.cs:831-842`). Other games' channels follow the same `#cncnet-{id}` / `#cncnet-{id}-games` pattern
(exceptions: rr/re/cncr, see `GameCollection.cs:61-89`).

### 3.2 Game-room channel name (`CnCNetLobby.cs:1167-1179`)

```
channelName = "#cncnet-yr" + "-game" + random.Next(1000000, 9999999)
```

⇒ `#cncnet-yr-game` + a 7-digit decimal in `[1000000, 9999998]`. Retried (max 10000) until no listed game in the
local game list has the same `ChannelName`. Same generator for loaded games (`:1122`).

### 3.3 Game-room key/password (`CnCNetLobby.cs:1091-1107`)

```
if user password empty:  password = CalculateSHA1ForString(channelName).Substring(0, 10); isCustomPassword=false
else:                    password = user password (UI max 20 chars, GameCreationWindow.cs:118); isCustomPassword=true
```

Joiners of a non-passworded game compute the same `sha1(channelName)[0:10]` (lowercase hex of the lowercase channel
name) (`CnCNetLobby.cs:949-953`). Joiners of a passworded game are prompted or get the password from an INVITE
(§8). Loaded games use `sha1(spawnSG.ini [Settings]GameID)[0:10]` (`:955-959`, `GameCreationWindow.cs:267-268`).

### 3.4 Joins after `001` (`CnCNetLobby.cs:1235-1268`)

Order of `Channel.Join()` calls: `#cncnet`, then local chat `#cncnet-yr`, then broadcast `#cncnet-yr-games`, then
broadcast channels of "followed" games. For persistent channels `Join()` queues the line with a **random delay of
1..9999 ms** (`Channel.cs:352-359`), so the actual send order is random:

```
JOIN #cncnet ra1-derp
JOIN #cncnet-yr ra1-derp
JOIN #cncnet-yr-games
```

(`"JOIN " + ChannelName + " " + Password"` or without key; type `SYSTEM_MESSAGE`, priority 9.) Leaving a persistent
channel: `PART {chan}` with the same random delay (`Channel.cs:375-388`); `CnCNetManager.LeaveFromChannel` sends
`PART {chan}` with priority 10, no delay (`CnCNetManager.cs:160-166`).

### 3.5 Creating a game – every line the host sends, in order

1. `CnCNetLobby.Gcw_GameCreated` (`:1086-1115`): create `Channel(uiName=gameRoomName, channelName, persistent=false,
   isChatChannel=true, password)`, register the lobby, then **immediately** (type `INSTANT_MESSAGE` = written to the
   socket synchronously, bypassing the queue, `Connection.cs:1047-1048`):
   ```
   JOIN #cncnet-yr-game1234567 {password}
   ```
2. Server echoes `:{host}!{ident}@{addr} JOIN #cncnet-yr-game1234567` (plus 353/366). The echo with our own nick
   triggers `CnCNetGameLobby.Channel_UserAdded` (adds host as `Players[0]`, `Ready=true`, `:1006-1056`) and then
   `CnCNetLobby.GameChannel_UserAdded` ⇒ `gameLobby.OnJoined()` (`:1043-1054`).
3. `OnJoined()` host branch (`CnCNetGameLobby.cs:412-434`), both queued `SYSTEM_MESSAGE` priority 50:
   ```
   MODE #cncnet-yr-game1234567 +klnNs {password} {playerLimit}
   TOPIC #cncnet-yr-game1234567 :B;yr
   ```
   Format strings: `"MODE {0} +klnNs {1} {2}"` (`:422`), `"TOPIC {0} :{1}"` with
   `CNCNET_PROTOCOL_REVISION + ";" + localGame.ToLower()` (`:427-428`; `CNCNET_PROTOCOL_REVISION = "B"`,
   `ProgramConstants.cs:29`). `playerLimit` = max players chosen (2..8, `GameCreationWindow.cs:85`).
   Mode letters: `k` key, `l` user limit, `n` no external messages, `s` secret, `N` – ircu/GameSurge-specific,
   meaning **UNVERIFIED** (the protocol depends on non-op members being able to send channel NOTICEs, so it is not a
   notice block for members).
   The host relies on the ircd auto-opping the channel creator; no ChanServ/AuthServ/`MODE +o` is sent anywhere
   (UNVERIFIED that GameSurge auto-ops, but the code has no alternative path).
4. Broadcast timer armed: first `GAME` CTCP 10 s later, then every 30 s (§5.3).
5. Then, via `UpdatePing()` (`:444`, `:448-453`), if a static tunnel is selected:
   `NOTICE #cncnet-yr-game1234567 :\x01TNLPNG {ms}\x01` (priority 10).

Changing settings later (`CnCNetGameLobby.cs:654-748`): password ⇒ `Channel.ChangePassword` (`Channel.cs:313-336`):
`MODE {chan} -k {old}` / `MODE {chan} +k {new}` / `MODE {chan} -k+k {old} {new}` (all `INSTANT_MESSAGE`);
max players ⇒ `MODE {chan} +l {n}` (`Channel.cs:343-347`); then CTCP `GSETTINGS {name};{maxPlayers};{skillLevel};{isCustomPassword 0|1}`
(`:755-764`, type `GAME_SETTINGS_MESSAGE`, priority 11) and an immediate `GAME` broadcast (`:747`).

Lock / unlock (`CnCNetGameLobby.cs:2624-2644`), both `INSTANT_MESSAGE`:
```
MODE {chan} +i        (lock)
MODE {chan} -i        (unlock)
```
Joiners detect lock state purely from the `MODE` line with mode string exactly `"+i"` / `"-i"`
(`:1088-1103`). The host auto-locks when `Players.Count >= playerLimit` (`:1048-1052`) and auto-unlocks when
someone leaves (`:1075-1078`).

### 3.6 Leaving / closing a room

`LeaveGameLobby()` (`CnCNetGameLobby.cs:876-887`): host sets `closed = true`, sends one final `GAME` broadcast with
the closed flag = `1` (§5.2), then `Clear()` (unregisters the channel), then `channel.Leave()` ⇒
`PART #cncnet-yr-game1234567` (non-persistent ⇒ no delay, `SYSTEM_MESSAGE` prio 9). No `MODE`, no `KICK` of
remaining users. Non-hosts leave with just `PART`. The host does not send anything to `#cncnet-yr-games` other
than the closed `GAME` CTCP; the lobby also drops the game when the host `PART`s/`QUIT`s/is `KICK`ed from the
**broadcast channel** (`CnCNetLobby.cs:1532-1543`) or when no refresh arrives for 35 s (§5.4).

Host inactivity: `InactiveHostKickSeconds=300` in the installed `ClientDefinitions.ini`; a `GameHostInactiveChecker`
calls `LeaveGameLobby()` (`CnCNetGameLobby.cs:400`) – client-side only, not a server rule; disabled when a custom
password is set (`:402-408`).

---

## 4. CTCP encoding, parsing, queue, rate limiting

### 4.1 Sending (`Channel.cs:278-285`)

```
"NOTICE " + ChannelName + " " + (char)58 + (char)1 + message + (char)1
```
⇒ `NOTICE #chan :\x01{message}\x01`. Always NOTICE (never PRIVMSG) for channel CTCPs. Private CTCPs to a user are
sent as `PRIVMSG {nick} :\x01{message}\x01` (`GlobalContextMenu.cs:124-126`, `CnCNetLobby.cs:1321-1323`).

Plain chat: `PRIVMSG {chan} :\x03{NN}{text}` with `NN = colorId.ToString("D2")` (two digits, 0..15, palette
`CnCNetManager.cs:61-79`) (`Channel.cs:261-269`; type `CHAT_MESSAGE`, priority 0). Private chat:
`PRIVMSG {nick} :{text}` with no color (`PrivateMessagingWindow.cs:535-536`).

### 4.2 Receiving (`Connection.cs:670-697`, `:716-740`)

- `NOTICE` with a prefix containing `!` and `params[1][0] == '\x01'` ⇒ CTCP. Payload = `params[1]` minus first
  char and minus the **last char** (`ctcpMessage.Remove(0,1).Remove(len-2)`, `:679`) – so the trailing `\x01` must be
  the very last byte. Sender = prefix up to `!`. Delivered as `OnCTCPParsed(params[0], sender, payload)`.
- `PRIVMSG` whose trailing starts with `\x01` and does **not** contain `ACTION` is routed to the same CTCP path
  (`:717-720`). `\x01ACTION ...\x01` is unwrapped and shown as chat (`:727-728`).
- A NOTICE without `!` in the prefix (server notice) is only displayed (`:693-696`); a non-CTCP user NOTICE is dropped
  (`OnNoticeMessageParsed` is a no-op, `CnCNetManager.cs:534-537`).
- Dispatch (`CnCNetManager.cs:380-399`): if `params[0]` is a registered channel ⇒ `Channel.CTCPReceived`; else if
  `params[0] == PLAYERNAME` ⇒ `PrivateCTCPReceived`; otherwise ignored.
- Line framing (`Connection.cs:519-546`): split on `\n`; the character before `\n` is **always discarded**
  (assumed `\r`); residual `\r` removed again (`:555`). Prefix/command/params parse: `ParseIrcMessage` (`:812-865`) –
  prefix = after leading `:` up to first space; trailing = everything after the first `" :"` occurrence; middle params
  split on spaces. Note: a trailing search for `" :"` is done on the whole line, so a middle param can never contain
  `" :"`.
- "Command from host" is decided per handler by comparing the CTCP sender nick with `hostName` (the nick that sent
  the `GAME` broadcast / that created the room): `CnCNetGameLobby.cs:1540`, `:1908`, `:2443`, `:2451`, `:2589`,
  `:2679`. Messages from other nicks for host-only commands are silently ignored. Host-side handlers (`OR`, `R`,
  `FHSH`) accept any nick that is in `Players` (`:1395`, `:1465`, `:2574`).
- Game-room CTCP command matching (`CommandHandlers/*.cs`): `NoParamCommandHandler` requires `message == name`;
  `StringCommandHandler`/`IntCommandHandler` require `message.StartsWith(name)` and `message.Length >= name.Length+1`,
  argument = `message.Substring(name.Length + 1)` (the separator char is skipped, not checked – it is always a space
  in practice). Handlers are tried in the array order of `CnCNetGameLobby.cs:75-108`; the first that returns true
  wins. Because prefix matching is unanchored, `RETURN`/`RENEGALL` first hit the `IntCommandHandler("R")`, which
  rejects them only because `"TURN"`/`"NEGALL ..."` fail `int.TryParse`; a bot adding commands must avoid names
  whose prefix is an existing command followed by something that parses as the expected argument type.

### 4.3 Send queue (`Connection.cs:871-920`, `:1020-1090`)

- One sender thread. Loop: take the first queue entry whose delay has elapsed (`Delay <= 0` or `SendAt < now`),
  write it, then **sleep `SendSleep` ms** (`MessageQueueDelay`, `:158`, `:911`). `SendSleep` =
  `[Settings] SendSleep` in `ClientDefinitions.ini`, default **2500 ms** (`ClientConfiguration.cs:219`); not set in the
  installed YR INI ⇒ 2500 ms between consecutive queued lines. Idle poll 10 ms.
- `INSTANT_MESSAGE` type bypasses the queue entirely and is written from the calling thread (`:1047-1048`) – used for
  `JOIN` of game rooms, `KICK`, `MODE` (ban/key/limit/lock), `CD`.
- Priority: new message is inserted **before the first queued message with a strictly lower priority**
  (`FindIndex(m => m.Priority < qm.Priority)`, `:1051`); equal priorities are FIFO. Higher number = sent earlier.
- "Special" types (`GAME_BROADCASTING_MESSAGE, GAME_PLAYERS_MESSAGE, GAME_SETTINGS_MESSAGE,
  GAME_PLAYERS_READY_STATUS_MESSAGE, GAME_LOCKED_MESSAGE, GAME_GET_READY_MESSAGE, GAME_NOTIFICATION_MESSAGE,
  GAME_HOSTING_MESSAGE, WHOIS_MESSAGE, GAME_CHEATER_MESSAGE, GAME_NEGOTIATION_MESSAGE`): if a message of the same
  type is still queued it is **replaced in place** (coalescing) (`:1068-1090`).
- `replace=true` (`QueuedMessage.Replace`, delay 1 ms, `QueuedMessage.cs:18-20`) does the same for any type (used
  for `PEO`, `CnCNetGameLobby.cs:1532`).
- Queue is cleared on disconnect (`:914-917`); `QueueMessage` is a no-op while not connected (`:1022-1023`).
- Priorities used (see sections): PONG 5000; MODE/TOPIC on room creation 50; `GAME` broadcast 20; `STRTD`/`RETURN`
  20; `GO`/`PO`/`PEO`/`GSETTINGS` 11; `FHSH`/`TNLPNG`/`STARTV2|3`/`MM`/`CHTNL` 10; JOIN/PART/WHO 9; map sharing 9;
  `OR` 6; `R` 5; chat/notifications/`DR`/`GETREADY` 0; `WHOIS` 0; lock/unlock `MODE` -1 (instant anyway).

### 4.4 Message length

No client-side truncation or splitting anywhere (`Channel.cs`, `Connection.cs`). UI limits only: chat input 200 chars
(`CnCNetLobby.cs:245`), game name 23 chars (`GameCreationWindow.cs:69`), password 20 chars (`:118`), game name
has `;` stripped (`NameValidator.cs:331-335`). The 512-byte IRC line limit is enforced by the server only; the code
comments (`CnCNetGameLobby.cs:1492-1494`) pack player options into ints specifically "to make GameSurge kicking
people a bit less common" (flood limits). UNVERIFIED: GameSurge's exact flood/line limits.

---

## 5. The `GAME` broadcast CTCP

### 5.1 Where/when

Sent by the host to the local game's broadcast channel (`#cncnet-yr-games`) as
`NOTICE #cncnet-yr-games :\x01GAME ...\x01`, type `SYSTEM_MESSAGE`, priority 20 (`CnCNetGameLobby.cs:3095-3157`).
Not sent if the broadcast channel has > 500 users **and** the host is in-game (`:3102-3103`).

### 5.2 Exact payload (`CnCNetGameLobby.cs:3105-3154`) – fields `;`-separated, **15 fields** in this checkout

```
GAME {rev};{gamever};{maxPlayers};{channel};{roomName};{flags};{players};{map};{gameMode};{tunnel};{loadedGameId};{skillLevel};{mapSha1};{options};{difficulty}
```

| # | field | value / type | sender code | receiver parse (`CnCNetLobby.cs`) |
|---|---|---|---|---|
| 0 | rev | `CNCNET_PROTOCOL_REVISION` = `B` | `:3106` | `:1599-1601` must equal `B` or message ignored |
| 1 | gamever | `GAME_VERSION` string, e.g. `9.3.5` | `:3108` | `:1602`; `!= local` ⇒ `Incompatible` (`:1725`) + yellow warning on join; join refused only if `DisallowJoiningIncompatibleGames` (`:879`) |
| 2 | maxPlayers | int 2..8 | `:3110` | `:1603` `IntFromString(,0)` |
| 3 | channel | room channel name (lowercase) | `:3112` | `:1604`; used for JOIN and for `HostedCnCNetGame.Equals` |
| 4 | roomName | display name (no `;`) | `:3114` | `:1605` |
| 5 | flags | exactly 5 chars, each `0`/`1`: `[0]` locked, `[1]` custom password, `[2]` closed, `[3]` loaded game, `[4]` ladder | `:3116-3123` (host lobby always writes `0` for [3],[4]) | `:1606-1610` via `Substring(i,1)`; defaults if unparsable: locked=true, closed=true |
| 6 | players | nick list joined by `,` (host first; trailing comma removed) | `:3125-3131` | `:1611` split on `,`, empty entries removed; `hg.Players` |
| 7 | map | `Map.UntranslatedName` or empty | `:3133` | `:1613` |
| 8 | gameMode | `GameMode.UntranslatedUIName` or empty | `:3135` | `:1614` |
| 9 | tunnel | `{address}:{port}` of the selected tunnel, `0.0.0.0:0` if none, or literal `[DYN]` for V3-dynamic mode | `:3137-3142` | `:1616-1658`: if not `[DYN]`, the `addr:port` **must exist in the joiner's tunnel list** (`tunnelHandler.Tunnels`, loaded from `https://cncnet.org/master-list`, versions 2/3/4 only, `TunnelHandler.cs:42`, `:385`) or the game is silently dropped |
| 10 | loadedGameId | always `0` from the normal lobby | `:3144` | `:1660` stored as `MatchID` |
| 11 | skillLevel | int index | `:3146` | `:1661-1662` normalized |
| 12 | mapSha1 | `Map.SHA1` (may be empty) | `:3148` | `:1663` |
| 13 | options | packed broadcastable game options: `,`-joined ints – first `ceil(nCheckboxes/32)` ints are bit-packed checkbox states (LSB = first checkbox), then one int per broadcast dropdown; empty string if nothing is broadcastable (`GameLobbyBase.cs:103-129`) | `:3150-3152` | `:1665-1707` only parsed when `gamever == local` and channel is the local broadcast channel |
| 14 | difficulty | `ddDifficulty.SelectedIndex` or `-1` | `:3154` | `:1709` `int.Parse` (throws on empty ⇒ message dropped, caught at `:1767`) |

Receiver rejects the whole message unless `Split(';').Length == 15` (`:1578`). **Field count is client-version
dependent**: git history of this file shows 12 fields after upstream #598 (skill level), 13 after #825 (map hash),
14 after #874 (options), 15 after #802 (difficulty). The bot must emit the count that the target client
population parses – UNVERIFIED which count the currently distributed CnCNet YR client uses; check its
`CnCNetLobby.cs`. Also note the `GAME` is only accepted if the sender nick is in the broadcast channel's user list
(`:1549-1552`) – the host must have joined `#cncnet-yr-games` and been seen via JOIN or NAMES.

Loaded-game lobby variant (`CnCNetGameLoadingLobby.cs:850-890`): same 15 fields, flags `[3]`=`1`, players = saved
game players, skill level = default, options = saved values, difficulty = `CoopDifficulty`.

Concrete example:
```
NOTICE #cncnet-yr-games :\x01GAME B;9.3.5;8;#cncnet-yr-game4815162;Bob's Game;00000;Bob;Heck Freezes Over;Battle;51.75.86.246:50000;0;1;3f2a...;0,2;-1\x01
```

### 5.3 Frequency (`CnCNetGameLobby.cs:37-39`, `:431-433`, `:3092-3093`)

`INITIAL_GAME_BROADCAST_DELAY` 10 s after the room is created, then `GAME_BROADCAST_INTERVAL` every **30 s**
(`XNATimerControl`, auto-reset). `AccelerateGameBroadcasting()` subtracts 10 s from the countdown on lock/unlock
(`:2631`, `:2643`). An immediate out-of-band broadcast happens on settings change (`:747`) and on leave with
closed=1 (`:882`). Timer is UI-driven (`Update(GameTime)`), so no broadcast while the UI thread is blocked.

### 5.4 Lobby-side list semantics (`CnCNetLobby.cs:1711-1765`, `GameListBox.cs`)

- Games are keyed by **host nick** (`FindIndex(hg => hg.HostName == e.UserName)`), not by channel: one game per nick;
  a new `GAME` from the same nick replaces the entry.
- `closed` flag (`flags[2] == '1'`): remove the game of that host and dismiss invitations (`:1731-1744`). Any other
  `GAME` adds/refreshes with `LastRefreshTime = now` (`:1721`).
- Removal also on host `PART`/`QUIT`/`KICK` seen in the broadcast channel (`:1532-1543`), and by age: every 1 s
  entries with `now - LastRefreshTime > 35 s` are dropped (`GameListBox.cs:26`, `:56`, `:408-423`). So a 30 s
  interval leaves a 5 s margin; a stalled sender queue (2.5 s per line) makes games flicker.
- `locked` ⇒ join refused client-side (`:882-883`); `473` from the server when the room is `+i`.
- `UPDATE {version}` CTCP in the local broadcast channel from a channel **op** (`@`) prompts the user to update if
  `version != GAME_VERSION` (`:1554-1570`).

---

## 6. JOIN of a game room – what the joiner does and expects

Joiner side (`CnCNetLobby.cs:967-997`, `CnCNetGameLobby.cs:297-359`, `:412-446`):

1. Creates `Channel(roomName, channelName, persistent=false, chat=true, password)`; registers the game lobby with
   `hostName = hg.HostName` (= nick that sent the `GAME`), `playerLimit = hg.MaxPlayers`, `tunnel = hg.TunnelServer`.
2. Sends immediately: `JOIN {channel} {password}` (`INSTANT_MESSAGE`, `:995-996`). Password is the custom one or
   `sha1(channel)[0:10]`.
3. Expected server responses: own `JOIN` echo, `353`/`366`, `332` (topic, optional – not required, only stored),
   `MODE` lines. Errors: `475` ⇒ "Incorrect password", `473`/`471` ⇒ "locked" (game marked locked), `474` ⇒ banned,
   `439` ⇒ abort; each clears the join attempt.
4. On own `JOIN` echo: `CnCNetGameLobby.Channel_UserAdded` adds itself to `Players`, then `OnJoined()` sends
   `NOTICE {chan} :\x01FHSH {hash}\x01` (priority 10) and, if a static tunnel is selected,
   `NOTICE {chan} :\x01TNLPNG {ms}\x01` (priority 10). `{hash}` = `FileHashCalculator.GetCompleteHash()` (game file
   hash; out of scope here).
5. On `353`: if `hostName` is not in the channel user list the joiner leaves ("host has abandoned") (`:989-999`).
   The host must therefore be in the channel before anyone joins.
6. Expected from the host **after** each JOIN (host side `:1031-1041`): `ChangeMap(...)` ⇒ `GO ...` (game options,
   `:1856-1901`), `PO ...` (player options, `:1480-1517`), `PEO ...` (`:1525-1533`). Until a `GO` arrives the joiner
   has `Map == null` and cannot ready up (`:1355-1366`). All three are accepted only from `hostName`.
   - `GO` format (`:1876-1900`): `GO ` + `;`-joined: `ceil(nCheckboxes/32)` packed checkbox ints, one int per
     dropdown (`SelectedIndex`), `mapOfficial 0|1`, `mapSha1`, `gameModeName`, `FrameSendRate`, `MaxAhead`,
     `ProtocolVersion`, `RandomSeed`, `RemoveStartingLocations 0|1`, `mapUntranslatedName`, `(int)tunnelMode`,
     `difficultyIndex|-1`. Receiver requires `parts.Length >= checkBoxInts + nDropDowns + 11` (`:1913-1922`) and
     identifies the map by `(gameModeName, mapSha1)` (`:1959`); checkbox/dropdown counts must match the joiner's
     `GameOptions.ini` – so the bot must replicate the exact option layout of the target client.
   - `PO` format (`:1483-1516`): `PO ` + per player `{nick};{packed};{ready};` where `packed` = little-endian int of
     bytes `(team, start, color, side)` and `ready` ∈ `0|1|2` (2 = auto-ready); AI players: `{aiLevel};{packed};` (no
     ready field). Receiver treats a numeric first token as AI (`:1558-1566`) and skips humans not in the channel user
     list (`:1575-1579`).
   - `PEO` format: `PEO ` + 5 chars `0|1` (forceRandomSides, forceRandomColors, forceNoTeams, forceRandomStarts,
     useTeamStartMappings) + separator + mappings (`PlayerExtraOptions.cs:44-60`).
7. Joiner ⇒ host afterwards: `OR {int}` (option request, bytes `(side,color,start,team)` LE, `:1340-1352`),
   `R {0|1|2}` (ready, `:1379`), chat `PRIVMSG`, `RETURN`/`STRTD`, map-sharing `MAPREQ|MAPOK|MAPFAIL|MAPSDISABLED`.
   Host answers `OR`/`R` with a fresh `PO`.
8. Host ⇒ all: `STARTV2 {gameId};{nick};{tunnelAddr}:{port};...` or `STARTV3 {gameId};{payload}` (`:1235-1257`),
   notifications `GETREADY AISPECS INSFSPLRS TMPLRS CLRS SLOC LCKGME NVRFY {i} INGM {i}` (`:2457-2531`), `MM {nick}`
   (file hash mismatch, `:2582`), `CD`, `DR {sides},{r1,r2..}`, `CHTNL {addr}:{port}`, `RENEGALL ...`, `NEGRPT ...`,
   `GSETTINGS ...`.

Host side learning the joiner: the `JOIN` line `:{nick}!{ident}@{host} JOIN {chan}` gives nick/ident/host
(`Connection.cs:698-706`); `Channel_UserAdded` fires per JOIN. If the joiner pushes `Players.Count` over
`MAX_PLAYER_COUNT` (8, `GameLobbyBase.cs:47`) or `playerLimit`, the host immediately `KICK`s (`:1009-1015`).
Channel user list from `353` carries nicks only (no ident/host).

---

## 7. Kick and ban

Raw ops only; no ChanServ/X/services commands exist in the code.

```
KICK {chan} {nick}                      Channel.cs:292-295   ("KICK " + ChannelName + " " + userName), INSTANT_MESSAGE
MODE {chan} +b *!*@{host}               Channel.cs:302-306   string.Format("MODE {0} +b *!*@{1}", ChannelName, host), INSTANT_MESSAGE
```

No kick reason. Ban (`CnCNetGameLobby.cs:2657-2672`): looks up `connectionManager.UserList` by nick, sends the
`+b` with `user.Hostname` **then** the `KICK`. `Hostname` is only populated from a `JOIN` prefix (first time the nick
was seen) or a `WHO`/`WHOIS` reply; if the user was first learned via `353` in a chat channel and never WHO'd,
`Hostname` is null and the client emits `MODE {chan} +b *!*@` (empty mask) – a bot should run its own `WHO`/`WHOIS`.
Kick from UI (`:2646-2655`) = `KICK` only. Priority arg 8 is irrelevant for `INSTANT_MESSAGE`.

Joiner side on `KICK` of itself: numeric-less `KICK` line ⇒ `OnUserKicked` ⇒ lobby closed (`:965-975`). On `474`
when re-joining: "banned by the game host".

---

## 8. Lobby private messages / CTCPs

- Game invite (`GlobalContextMenu.cs:117-126`; constants `ProgramConstants.cs:100-101`):
  ```
  PRIVMSG {nick} :\x01INVITE {channel};{roomName}\x01                 (no password)
  PRIVMSG {nick} :\x01INVITE {channel};{roomName};{password}\x01      (custom password only)
  ```
  type `CHAT_MESSAGE`, priority 0. Receiver (`CnCNetLobby.cs:1294-1384`): 2 or 3 `;`-fields; ignored unless the
  sender shares a channel with us and is not ignored; if it cannot join it replies
  `PRIVMSG {sender} :\x01INVITATION_FAILED\x01` (`:1321-1323`). Accepting looks up the game **by channel name** in the
  current game list, so the inviter's `GAME` broadcast must already be listed.
- Private chat: `PRIVMSG {nick} :{text}`; received when `params[0] == PLAYERNAME` (`Connection.cs:733-734`),
  formatting stripped (`CnCNetManager.cs:547`).
- `TNLPNG {ms}` is a channel CTCP in game rooms (§6), not a PM. `WHOIS {nick}` on context-menu open.

---

## 9. Quirks and constants

- **Nick validation** (`NameValidator.cs:77-126`): non-empty, not offensive (word list), first char not a digit and
  not `-`, allowed chars exactly `abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_[]|\{}^`` ` (no
  spaces, no `~`), length `<= MaxNameLength` (12 or 15 in installed YR clients). Offline names strip `,;`
  (`:134-144`). Server-side NICKLEN is not checked by the client (UNVERIFIED; ircu default is 30).
- **Ident** appears to others as `~YR.xxxxxx`; the client strips `~` when deriving the game id
  (`CnCNetManager.cs:612`). GameSurge hostname cloaking / `+x`: not referenced anywhere – UNVERIFIED whether the
  `host` part in JOIN prefixes is a real IP, a `*.user.gamesurge` cloak, or both; the ban mask uses whatever the
  JOIN/WHO reply gave.
- No CnCNet-specific IRC commands (no `CNCNET-INFO` or similar) exist in the client; the only non-RFC strings on
  the wire are the CTCP payloads above and the `ra1-derp` channel key.
- `MODE` parsing on the client handles only `o` and `v` (`CnCNetManager.cs:265-307`); any other letters are ignored
  except that the game lobby compares the raw mode string against `"+i"`/`"-i"` (`CnCNetGameLobby.cs:1090`,
  `:1098`) – a combined mode change like `+ki` would **not** be recognised as a lock.
- `353` prefixes recognised: `@` op, `+` voice only (`CnCNetManager.cs:766-775`); a nick in a JOIN prefix starting
  with `@` is also treated as op (`:584-588`, dead code in practice).
- `NICK` changes are tracked (`Connection.cs:776-785`, `CnCNetGameLobby.cs:899-910`); the game list and `hostName`
  are **not** updated on a host nick change (keyed by the old nick) – a host bot must never change nick while hosting.
- `TOPIC` of game rooms is `B;yr` (revision;game id) and is never read back by anyone except for display
  (`CnCNetManager.cs:309-327`).
- Supported tunnel versions for the broadcast `tunnel` field: 2, 3 and 4 (matchmaking) per `TunnelHandler.cs:40-42`,
  `:385`; master-list line format `addr:port;country;cc;name;reqPw;clients;max;status;lat;lon;version;distance`
  (`CnCNetTunnel.cs:46-78`).
- Player-count HTTP: `CnCNetLiveStatusIdentifier=cncnet5_yr` with `https://api.cncnet.org/status` – HTTP, not IRC.
- The client ignores IRC messages it cannot parse (whole `PerformCommand` is in a `try/catch`, `Connection.cs:788-791`).
