# Protocol reference

Extracted from `xna-cncnet-client` at commit 64cfd548 (2026-11) with file:line cites. The
bot implements the host side of all three documents.

- `irc-layer.md` — servers, registration, ident, channels, CTCP framing, send queue, GAME broadcast, kick/ban.
- `game-lobby.md` — every game-room CTCP command, PO/GO/PEO formats, option layout, launch rules, map data.
- `tunnels.md` — master list, V2, V3 static, V3 dynamic negotiation, what a non-playing host must do.

## Live client dialect (captured 2026-11-05 from `#cncnet-yr-games`)

The deployed YR client is `9.3.5` and differs from the source the documents describe:

| item | 9.3.5 on the wire | develop source (64cfd548) |
|---|---|---|
| GAME revision letter (field 0, room topic) | `A` | `B` |
| GAME field count | 14 (no difficulty field) | 15 |
| GO trailing fields | 10 (no difficulty): `official;sha1;mode;fsr;maxahead;proto;seed;rmstarts;map;tunnelMode` | 11 |
| GO option fields | 1 packed check-box int + 6 drop-downs (`gamedata/options.json` matches) | same |
| GAME options field | `packedBroadcastBoxes,cmbCredits,cmbSuperWeaponsModifier,cmbCratesModifier,cmbGameSpeedCapMultiplayer` | same |
| V3 dynamic (`[DYN]`) | in use | same |

Older clients still online send `R9`/`R13`/`R14` revisions with 11/13/14 fields; they cannot
join 9.3.5 games either way. The bot adopts the dominant (revision, version, field count)
it observes, so a client update that changes these needs no bot change; a change to the
option controls does (rebuild `gamedata/options.json` and verify with `hostbot capture <nick>`).
