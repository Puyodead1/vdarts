# VDarts H2 online lobby ("remote PK") protocol

This is the TCP protocol behind the game's **Online → Online PK** lobby. The game connects to `ServerIpControl.ONLINE_IP:ONLINE_PORT`, which is `online.vdarts.net:8008` by default. With the `[Lobby]` section of VDartsServerRedirect, the game connects to vdarts-server's `Lobby.cs` instead.

How the protocol was rebuilt: **only from the client code**. The sources are the decompiled `Assembly-CSharp-firstpass.dll` (`fp/`) and `Assembly-CSharp.dll` (`as/`); file:line references point into those decompilations. The official server was never sent a login or any token. The one test against it was a bare TCP connect without login (2026-11-17), and the only thing it sent back was a `\r\n` keep-alive about every 2 s.

Statements marked **inferred** describe what a server has to do for the client to work, not something observed. Section 9 says what vdarts-server actually does.

Verified on 2026-11-17 against vdarts-server:
- A scripted pair of clients covered login, list, add, decline, cancel, unknown target, a full 501 relay, talk, game_over plus the idle `add` messages, mid-game disconnect, and the heartbeat.
- The real game (VDartsH2_V1, Windows) played a complete online 501 against a scripted client: lobby, invite, game start, both players' darts, checkout, result screen, and back to the lobby.

---

## 0. Transport and framing

| Item | Value | Source |
|---|---|---|
| Host / port | `online.vdarts.net:8008`, plain TCP, no TLS | fp/ServerIpControl.cs:256-258, fp/Online.cs:234 |
| Framing | One JSON object per line. The client writes with `StreamWriter.WriteLine`, so the line ends in `\r\n` on Windows builds and `\n` on Android. The server must strip `\r`. | fp/Online.cs:353, 266 |
| Server→client framing | The client uses `StreamReader.ReadLine()`, so `\n`, `\r\n` and `\r` all work. | fp/Online.cs:379 |
| Encoding | UTF-8 with no BOM on both sides (.NET defaults). Nicknames may be non-ASCII. | fp/Online.cs:237-238 |
| Connection lifetime | One TCP connection per lobby session. It lives in a `DontDestroyOnLoad` object, so it **stays open across scene changes** (lobby, game, GameOver, back to lobby). It closes when the user goes back to the main menu. | as/onlineMenuSystem.cs:84-97, as/initSystem.cs:143-169, 204 |

### 0.1 Client JSON parser quirks (I2.Loc.SimpleJSON). The server must follow these.

- Every scalar is stored as a **string**. Quoted and unquoted values parse the same way (`5`, `"5"`, `true` and `null` become `"5"`, `"true"`, `"null"`). fp/I2.Loc.SimpleJSON/JSONNode.cs:258-420
- `node.ToString()` on a scalar returns it **with quotes** (`"\"5\""`). fp/I2.Loc.SimpleJSON/JSONData.cs:46-49. A missing key returns a `JSONLazyCreator`: `.Value == ""`, `.ToString() == ""`, `.Count == 0`, and `== null` is true. fp/I2.Loc.SimpleJSON/JSONLazyCreator.cs:156-187, fp/I2.Loc.SimpleJSON/JSONNode.cs:32-41, 204-220
- **A value that is an empty string `""` is never stored** (`if (text != "")`), so the key behaves as missing. JSONNode.cs:328-338, 365-374
- Whitespace outside strings is ignored. `\uXXXX` and the usual escapes are supported. JSONNode.cs:379-420
- Never send JSON `null` for a field the client reads. It becomes the string `"null"`. Example: logo `"null"` is treated as a relative portrait path (fp/OnlinePlayer.cs:54-65).
- Many receive paths call `int.Parse(x.Value)` with no guard. A missing or non-numeric value throws inside `onlineMenuSystem.Update()`. That frame aborts and **the message is lost**, sometimes after the state has already changed. The required numeric fields are listed for each message below.

### 0.2 Client threads and queues

- **ReadThread** (fp/Online.cs:372-421): loops on `ReadLine()`.
  - Sets `lastRecvTime = Now` on **every** line, including empty ones (line 380).
  - An empty line causes `Sleep(20)` and a continue (381-385). Empty lines are a valid heartbeat.
  - `type == "repeat"`: this is the ACK. If `content.time` equals the `time` of the outstanding `lastSend`, the client clears `lastSend` (388-397).
  - `type == "target_not_exist"`: clears `lastSend` (this acts as an ACK) and sets `OnlinePlayer.SelectedPlayer = null` (399-403).
  - Anything else goes into the `Online.Recv` queue (405-406). This is a 64-slot FIFO; on overflow the oldest entry is dropped (fp/Online.cs:81-97). The queue has no lock.
  - After each non-empty line the thread calls `Sleep(100)` (408), so the client reads at most about 10 lines per second.
  - On EOF, `ReadLine()` returns null and `JSON.Parse(null)` throws. The exception is caught and the loop spins (410-418). **The client never notices that the server closed the socket.** `State` stays `Connected`/`InGame`.
- **WriteThread** (fp/Online.cs:328-370): stop-and-wait. See §1.
- **Main thread**: `onlineMenuSystem.Update()` takes **one** `Online.Recv` message per frame (as/onlineMenuSystem.cs:348). This is the **only** consumer of `Online.Recv` in the codebase (grep confirmed). It keeps running during games because the object persists.
- `OnlineMessage.Recv` (fp/OnlineMessage.cs:148-187): a 10-slot int FIFO, locked, oldest dropped on overflow. It carries remote darts into the game scenes. Value 87 means round end and 0..82 means a dart area. Only `list` clears it (as/onlineMenuSystem.cs:389).

---

## 1. Reliability layer: client→server ACK ("repeat")

- Every outgoing message carries `"time":"<n>"`. `n` is a **static, process-wide counter** that starts at 1 and increments after each `SendSocket` (fp/Online.cs:35, 322-326). It is **not** reset on reconnect, so `login` is `time:"1"` only on the first connect of the process.
- Outgoing messages are queued (`Online.Send`, 64-slot FIFO, fp/Online.cs:100-139). The WriteThread sends a message only when `lastSend == ""`, meaning the previous one has been ACKed (fp/Online.cs:344-357).
- If no ACK arrives, the same line (same `time`) is **resent every 99.75 s** (fp/Online.cs:349-350).
- After each write the thread sleeps 100 ms (fp/Online.cs:356).
- If a write throws, `lastSend = ""`: the message is **dropped**, not retried (fp/Online.cs:363-367).

**The server MUST ACK every client message** (login, invite, cancel_invite, response, round_start, round_end, hit, talk, game_over) with:

```json
{"type":"repeat","content":{"time":"<exact time value from the client message>"}}
```

The `time` must sit under `content`. Quoted or unquoted both match because of the parser rules in §0.1. Without the ACK, the client's entire outgoing stream stalls for about 100 s per message. Darts and round ends would freeze for the opponent.

Recommendations (inferred):
- ACK immediately, before any processing.
- Deduplicate by `time` per connection. A resend after 99.75 s carries the same `time`. Re-ACK it but do not re-process it.
- `exit` is written directly and bypasses the queue (fp/Online.cs:266-267). No ACK is needed. Close the session.
- The client never ACKs server messages. Server→client delivery is fire-and-forget.

---

## 2. OnlineState (fp/OnlineState.cs:1-10)

| Value | Int | Set where |
|---|---|---|
| `Null` | -1 | Initial value (fp/Online.cs:21). Also set by `Disconnect()` (fp/Online.cs:273). |
| `Disconnected` | 0 | Set when the TCP connect throws (fp/Online.cs:247). |
| `Connected` | 1 | Set after the TCP connect succeeds, before login (fp/Online.cs:239). Also set by: `inviteNo()` (as/onlineMenuSystem.cs:1306); a `cancel_invite` received while `Invited` (725); a `refuse` received while `Inviting` (738); the lobby Cancel button (as/onlineMenuEvent.cs:204); `GameOverSystem.Awake` (as/GameOverSystem.cs:88-91) and `KeyOK` (108, 125); quitting mid-game (as/OISystem.cs:1127, 1422; as/gameSystem.cs:362, 636, 1200; as/cricketSystem.cs:835, 1126; as/countUpSystem.cs:387, 514; as/MessageQuitGameSystem.cs:21; as/SettingPanelGroup.cs:382; as/VDartsPKSystem.cs:893; as/VDartsPK_BackToMenu.cs:43). |
| `Inviting` | 2 | Set after sending `invite` (as/onlineMenuEvent.cs:166; as/onlineGameExtraSetting.cs:125; as/onlineOISetting.cs:319; as/onlineMenuSystem.cs:252 for VCrown auto-invite). |
| `Invited` | 3 | Set when an `invite` is received in a normal lobby (not VCrown or League) (as/onlineMenuSystem.cs:572). |
| `Refuse` | 4 | **Never assigned anywhere.** It is only compared against (as/onlineMenuSystem.cs:1302; as/onlineMenuEvent.cs:200). |
| `InGame` | 5 | Invitee: set when it accepts (as/onlineMenuSystem.cs:1157, 1224, 1237, 1271). Inviter: set on `game_start` (784). Medley: set again at the start of every leg (as/VDartsPKSystem.cs:92). |

Notes:
- `ConnectThread` refuses to run when `State > Disconnected` (fp/Online.cs:224). Reconnecting requires `Disconnect()` first, which sets `State` to `Null`.
- The state is set to `Connected` before login completes, and login success does not change it.

---

## 3. Messages SENT by the client

All outgoing JSON is built by string concatenation in fp/Online.cs. The exact byte shapes, including the odd spaces, are shown below. Every value is a quoted string except where noted.

### 3.1 `login`: sent automatically right after the TCP connect

fp/Online.cs:162-165. It is called from `ConnectThread` (fp/Online.cs:250-257). The login variant with 4 arguments (fp/Online.cs:157-160) is never called.

```json
{"type":"login","content":{"token":"<hex token>", "account":"<account>", "version":"02.10", "auto":"false", "league_id":"0","data":{"isSteelBoard":"0", "isHomeDarts":"0", "Gambling":"T" } }, "time":"1"}
```

| Field | Value / meaning | Source |
|---|---|---|
| `token` | `PlayerInfoPool.tokens[0]` (hex-encoded form; HTTP APIs use `HexToASCII(token)`) | fp/Online.cs:257 |
| `account` | `PlayerInfoPool.loginedAccounts[0]` | fp/Online.cs:257 |
| `version` | `"02.10"`. `onlineMenuSystem.Start` overwrites the default `"1"` before connecting. | as/onlineMenuSystem.cs:167 |
| `auto` | `"false"` when the scene is `OnlineMenu` (a human in the lobby). `"true"` otherwise, which happens when `onlineMenuSystem.Init()` is called from inside a local game (as/OISystem.cs:1771-1775, the first-hit hook). That is an "auto"/background presence. | as/onlineMenuSystem.cs:157-166 |
| `league_id` | `Online.NowMatch_id`. Forced to `"0"` unless this is a VCrown/VCrownDouble session, where it is the VCrown match id. | fp/Online.cs:253-256 |
| `data.isSteelBoard` | `"1"`/`"0"` from `Game.isSteelBoard` | fp/Online.cs:250 |
| `data.isHomeDarts` | `"1"`/`"0"` from `Online.isHomeDarts` | fp/Online.cs:251 |
| `data.Gambling` | `"T"`/`"F"` from `Online.Gambling` (default true) | fp/Online.cs:53, 252 |

Expected replies: `repeat` (ACK), then `login_success` or `login_fail`/`login_without_token`, and a `list` (§4.2-4.4).
**Inferred:** the server should echo `version`, `auto`, `league_id` and `data` back to other clients inside the player objects (§4.4).

### 3.2 `invite`, variant A (plain lobby invite)

fp/Online.cs:167-170. `gamesetting` is inserted raw between braces.

```json
{"type":"invite","content":{"id":"<target lobby id>","game_setting":{<raw>},"game_type":"<code>"}, "time":"N"}
```

Call sites:
- as/onlineMenuEvent.cs:161-166, the lobby "ConfirmBtn". Used for Count Up (`101`), 501 against old clients with ver < 1.23 (`202`), Cricket without stake (`301`), Sniper (`603`), TicTacToe (`403`) and CricketHidden (`303`).
- as/onlineGameExtraSetting.cs:122-125, for Medley (`5`).

`game_setting` contents (as/gamesetting.cs:7-18, 44-72):
- CricketHidden (303): `"scorezone":[a,b,c,d,e,f,g]`. Seven distinct numbers in 1..20 or 25, generated by the inviter at as/onlineMenuEvent.cs:122. The numbers are unquoted.
- SuperV / Medley (5): `"types" : <702..708>, "legs": <3|5|7>`. Unquoted ints. The types come from as/onlineGameExtraSetting.cs:48-59.
- Every other type: empty, so the message contains `"game_setting":{}`.

### 3.3 `invite`, variant B (01 games with In/Out/Bull settings, staked games, VCrown, rematch)

fp/Online.cs:172-175.

```json
{"type":"invite","content":{"id":"<target id>","game_type":"<code>","vpoint_bet":"<int>","match_id":"<int>","isSteelBoard":"<0|1>","isHomeDarts":"<0|1>"}, "time":"N"}
```

Call sites:
- as/onlineOISetting.cs:264-319 (`OnlineInvitation`), reached from the 501/701 "MOD" settings dialog (as/onlineMenuEvent.cs:142-145), the stake dialog for Cricket (as/onlineMenuEvent.cs:151-154), and GameOver rematch (as/GameReMatch.cs:60-68).
  - `game_type`: for codes starting with `2` it is `"<202|203><in><out><bull>"`. Example: `"202012"` means 501, Open In, Double Out, Bull 50. It is built at as/onlineOISetting.cs:271-282. `in`/`out` are 0=Open, 1=Double, 2=Triple, 3=Master. `bull` is `DataMap.onlineOIBull`: 1 means outer bull counts 25, 2 means 50. Otherwise it is the plain code, such as `"301"`.
  - `vpoint_bet`: the stake. `"0"` means no stake.
  - `match_id`: `onlineMenuSystem.OnlineGambling_id`, the gambling match id from the HTTP `vpointBet.php` call. `"0"` means none.
  - `isSteelBoard`: `Game.isSteelBoard ? 1 : 0`.
  - `isHomeDarts`: always `0`. The private field is never assigned (as/onlineOISetting.cs:42).
- as/onlineMenuSystem.cs:247-253 (VCrown `AutoInvite`): `Invite(competitorID, "202"|"203"|"301", "0", match_id, 0, 0)`. Here `match_id` is the VCrown match id string.

### 3.4 `cancel_invite`

fp/Online.cs:177-180

```json
{"type":"cancel_invite","content":{"id":"<target id>"}, "time":"N"}
```

Sent by the inviter when it presses Cancel while `Inviting` (as/onlineMenuEvent.cs:183-188), or from `inviteNo()` while `Inviting` (as/onlineMenuSystem.cs:1288-1293). It is not sent if `SelectedPlayer` was nulled by `target_not_exist`.

### 3.5 `response`

fp/Online.cs:182-185

```json
{"type":"response","content":{"id":"<inviter id>","game_type":"<code>","response":"true"}, "time":"N"}
```

- `response` is only ever `"true"` or `"false"`. There is no `busy` value.
- `game_type` is `(int)OnlinePlayer.GameType`. **For 01 invites this is the bare `202`/`203`, not the 6-digit code the inviter sent** (as/onlineMenuSystem.cs:585, 652-661). The server must not require it to match the invite.
- `"true"` is sent from: `inviteYes()` with no stake (as/onlineMenuSystem.cs:1157-1161); after a successful HTTP deposit for staked games (1221-1229); `ExitMenu` (1232-1246); and the VCrown result callback (1268-1276).
- `"false"` is sent from:
  - `inviteNo()` while `Invited` (as/onlineMenuSystem.cs:1295-1301), which covers the user pressing No and the **10 s auto-decline** (`Invoke("inviteNo", GlobalData.ONLINEGAME_CANCEL_INVITE_TIME=10f)`, as/onlineMenuSystem.cs:709; fp/GlobalData.cs:95);
  - the lobby Cancel button while `Invited` (as/onlineMenuEvent.cs:190-198);
  - **any invite received while `State != Connected`** (busy or in game) (as/onlineMenuSystem.cs:712-715);
  - a VCrown invite from the wrong opponent (as/onlineMenuSystem.cs:557-563).
- **Some cases send no response at all.** `inviteNo()` called while `State == Connected` sends nothing. This happens to League players (as/onlineMenuSystem.cs:567-571) and in the "InviteCancelCount" scene filter (543-547). It also happens when inviteYes fails the coin check (1120-1126) or the vpoint check (1130-1134): `inviteNo` runs with state `Invited`, so in that case `false` **is** sent. **The server needs its own invite timeout** (inferred), because the inviter has no timeout (§6).

### 3.6 `round_start` / `round_end` / `game_over`

fp/Online.cs:187-200, 317-320

```json
{"type":"round_start", "time":"N"}
{"type":"round_end", "time":"N"}
{"type":"game_over", "time":"N"}
```

- `round_start`: sent when the **local** player's turn begins (as/OISystem.cs:829-832; as/gameSystem.cs:1313-1316; as/cricketSystem.cs:1952; as/countUpSystem.cs:330). The receiving client ignores it (as/onlineMenuSystem.cs:807-808).
- `round_end`: sent when the local player's turn ends. Triggers are: the OK/change key, auto-advance about 1.5 s after the 3rd dart (as/OISystem.cs:1813-1817, 1839-1849, where `m_removedarttime = GlobalData.OnlineGameRemoveDartTime = 1`), a bust, the idle timeout, or the last dart of the last round (as/OISystem.cs:770-773, 1808-1811; as/gameSystem.cs:1294-1297, 1467-1470; as/cricketSystem.cs:1571-1574, 1847-1850; as/countUpSystem.cs:273, 644).
- `game_over`: sent when the client leaves the online game for any reason:
  - normal end, pressing OK on the GameOver screen (as/GameOverSystem.cs:123-126; for Medley only after the last leg, 106-112);
  - mid-game quit (as/OISystem.cs:1128, 1423; as/gameSystem.cs:363, 637, 1201; as/cricketSystem.cs:836, 1127; as/countUpSystem.cs:388, 515; as/MessageQuitGameSystem.cs:22; as/SettingPanelGroup.cs:383; as/VDartsPKSystem.cs:894; as/VDartsPK_BackToMenu.cs:44);
  - insufficient money (as/OISystem.cs:3015-3018; as/cricketSystem.cs:3056; as/gameSystem.cs:1749; as/countUpSystem.cs:1206);
  - GameOver rematch, sent immediately before the rematch invite (as/GameReMatch.cs:62-67).

  It is often sent **after** `State` was already set to `Connected`. The server must accept `game_over` at any time and handle it idempotently.

### 3.7 `hit`

fp/Online.cs:202-205. Note that `area` is at the **top level**, not inside `content`.

```json
{"type":"hit","area":"<0..82>", "time":"N"}
```

Sent for every local dart while the local player is current. It is sent for any input code except -1 and 83..87 (as/OISystem.cs:1068-1071, which also requires `lockInvoke == 0`; as/gameSystem.cs:572-575; as/cricketSystem.cs:782-785; as/countUpSystem.cs:367).

**Area code** (fp/DataMap.cs:47-107):

| Code | Meaning |
|---|---|
| 0 | Miss |
| 1-20 | Single, inner small segment, number = code |
| 21-40 | Triple, number = code-20 (40 means T20) |
| 41-60 | Single, outer large segment, number = code-40 |
| 61-80 | Double, number = code-60 |
| 81 | Outer bull. Counts 25 or 50: in online 01 games `onlineOIBull` decides (1 means 25), otherwise 50 |
| 82 | Inner bull, 50 |

Key codes, which appear in local `Message.Recv` and are never sent as `hit`: 83=Up, 84=Down, 85=Left/Back, 86=Right, 87=OK/next player. A received `round_end` is injected as 87 (as/onlineMenuSystem.cs:787-789).

### 3.8 `talk`

fp/Online.cs:207-210. `number` is at the top level.

```json
{"type":"talk","number":"<0..6>", "time":"N"}
```

Sent from as/OnlineConversationsSystem.cs:272-285. Indices map to phrases at as/OnlineConversationsSystem.cs:46: 0 Hello, 1 GoodDarts, 2 OneMoreGame, 3 HurryUp, 4 NiceToMeetYou, 5 Excellent, 6 SeeYouNextTime. The client rate-limits to one talk per game (`canSendTalk`).

### 3.9 `exit`

fp/Online.cs:266-267. It is written synchronously, not queued, and not ACKed. The socket is closed right after it.

```json
{"type":"exit", "time":"N"}
```

Sent by `Online.Disconnect()`, called from: the lobby back button (as/onlineMenuEvent.cs:20-28); the timeout popup (as/onlineMenuSystem.cs:334-342); login failure (370); VCrown end (226); a scene change to the main menu (as/initSystem.cs:156-164); and `initSystem.OnDestroy` (as/initSystem.cs:204).

---

## 4. Messages RECEIVED by the client

All of them are dispatched in `as/onlineMenuSystem.cs:348-809` on `jSONNode["type"].Value`. `repeat` and `target_not_exist` are consumed earlier in the ReadThread (§0.2). Unknown types are logged and ignored (as/onlineMenuSystem.cs:804-806), so they are harmless.

### 4.1 `repeat` (ACK)

See §1. fp/Online.cs:388-397.

```json
{"type":"repeat","content":{"time":"7"}}
```

### 4.2 `login_success`

as/onlineMenuSystem.cs:374-380. It only hides the loading spinner. No fields are read.

```json
{"type":"login_success"}
```

### 4.3 `login_fail` / `login_without_token`

as/onlineMenuSystem.cs:357-373. The client hides the loading spinner, re-checks the token over HTTP (`PlayerInfoCtrl.CheckTokenVaild`, line 366), shows "OnlineMenu_UpdateVersion" (please update), and on OK calls `Disconnect()` and returns to the menu. No fields are read.

```json
{"type":"login_fail"}
```

**Timeout:** if neither `login_success` nor `list` arrives within **30 s** of connecting, and the scene is OnlineMenu, the client shows "NetCheckFail", disconnects and returns to the menu (as/onlineMenuSystem.cs:329-343). After that `isTimeout = true` and **all further messages are ignored** (344-347).

### 4.4 `list`: the full lobby roster

as/onlineMenuSystem.cs:381-457

```json
{"type":"list","players":[
  {"id":"17","nickname":"Bob","member_id":"421317","logo":"http://.../a.png","nation":"TW",
   "auto":"false","version":"02.10","league_id":"0","bhv":"idle",
   "rank":{"average":"12.34"},
   "data":{"isHomeDarts":"0","isSteelBoard":"1","Gambling":"T"}}
]}
```

Client actions:
1. Hides the loading spinner. Calls `OnlinePlayer.Clear()`, which also clears `SelectedPlayer`. **Calls `OnlineMessage.ClearRecv()` (line 389), which discards queued remote darts.** Never send `list` to a player who is in a game.
2. Stops if `players` is missing or empty (391).
3. For each entry it reads (398-404):
   - `id`: the lobby id. It is used as the target in invite, response and cancel_invite. **Inferred:** a server-assigned session id. It can be any string.
   - `nickname`.
   - `member_id`: used for HTTP `memberGet.php?id=` (fp/OnlinePlayer.cs:75), `fxPersonal.php`, and VCrown matching.
   - `logo`: empty means the default avatar. If it starts with `http` it is used as is. Otherwise it is prefixed with `http://api.vdarts.net:8080/picture/portrait/` (fp/OnlinePlayer.cs:54-65; fp/ServerIpControl.cs:262). **A non-empty logo shorter than 4 characters throws** (`Substring(0,4)`).
   - `nation`: stored as `Player.zone` and used for the flag texture (as/uiControl.cs:245, 250-254).
   - `auto`: parsed with `bool.TryParse`.
   - `version`: stored as `Player.ver`. Inviters compare it as a double. 701 requires ≥ 1.32. Sniper and TicTacToe require ≥ 1.24. 501 with In/Out settings requires ≥ 1.23 (as/onlineMenuEvent.cs:55, 67-117).
   - `data.isHomeDarts`: `"1"` shows H, anything else shows C. `data.isSteelBoard`: picks the icon. `data.Gambling`: `"T"` means the player accepts staked games (as/onlineOISetting.cs:200). See as/uiControl.cs:246-247.
   - `rank.average`: read via `Find("rank","average")` (399). It is overwritten later by the HTTP `memberGet.php` response (fp/OnlinePlayer.cs:71-84). **It must be numeric.** `CreateOnlineGame` does `(int)float.Parse(Regex.Replace(rank,"[_a-zA-Z]",""))` (as/onlineMenuSystem.cs:900, 947). A missing value gives null and then a crash when a game starts, unless the HTTP fetch filled it in.
   - `bhv`: `"fgt"` means in a game (busy). The player is shown with the busy icon, sorted last (fp/OnlinePlayer.cs:31-49), and cannot be invited (as/onlineMenuEvent.cs:47-52). Any other value means idle. **Inferred:** use `"fgt"` and anything else, such as `"idle"`.
   - `league_id`: only read in VCrown mode (424).
4. It starts an HTTP rank/background fetch per player, which then redraws the list (400).
5. In VCrown mode, if an entry's `member_id` equals player1 or player2 of the match and its `league_id == match_id`, that entry becomes `competitorID`. If the local user is player1, the client auto-invites 3 s later (409-455).

**Inferred:** the list should **exclude the receiving player itself**. Nothing on the client filters out self. In VCrown, self-inclusion would make the client select itself as its competitor.

### 4.5 `add`: a player joined or changed

as/onlineMenuSystem.cs:458-524. It has the same fields as one list entry, under `player`.

```json
{"type":"add","player":{"id":"17","nickname":"Bob","member_id":"421317","logo":"","nation":"TW","auto":"false","version":"02.10","league_id":"0","bhv":"idle","rank":{"average":"12.34"},"data":{"isHomeDarts":"0","isSteelBoard":"1","Gambling":"T"}}}
```

If the id already exists, the old entry is removed first (461-464) and the new one is added. **This is the only way to clear the busy flag of a single player** without sending a full `list`, because no "game_end" message exists. **Inferred:** after both sides of a game have sent `game_over`, broadcast `add` with `bhv` not equal to `"fgt"` for each participant. The UI redraw happens after the HTTP rank fetch (467).

### 4.6 `remove`: a player left

as/onlineMenuSystem.cs:530-538

```json
{"type":"remove","id":"17"}
```

The client removes the player, clears `SelectedPlayer` if it was that player, and redraws. **It does not abort an invite in progress or a game.** Use `refuse`/`cancel_invite`/`game_over` for those.

### 4.7 `game_init`: two players started a game (for lobby onlookers)

as/onlineMenuSystem.cs:525-529

```json
{"type":"game_init","players":[{"id":"17"},{"id":"23"}]}
```

The client marks `players[0].id` and `players[1].id` as `inGame` and redraws. **Both ids must exist in the receiver's list.** Otherwise `GetPlayer` returns null and the handler throws a NullReferenceException. The participants themselves are not in their own lists, so **do not send `game_init` to the two participants** (inferred), or accept the harmless exception it causes.

### 4.8 `invite`: someone invites this client

as/onlineMenuSystem.cs:539-716. **Fields are read at the TOP LEVEL, not under `content`.** The only exception is a debug log at line 545, which reads `content.id`. The server therefore has to **rewrite** the client's message instead of forwarding it.

```json
{"type":"invite","id":"<inviter lobby id>","game_type":"202012","game_setting":{},
 "vpoint_bet":"0","match_id":"0","isSteelBoard":"0","isHomeDarts":"0"}
```

Fields the client requires:

| Field | Use | Must parse |
|---|---|---|
| `id` | Inviter. Looked up with `OnlinePlayer.GetPlayer(id)` (582). **The inviter must be in the invitee's list.** Otherwise `OpponentPlayer` is null, line 583 throws, and the invitee is left stuck in `Invited`. | — |
| `game_type` | `int.Parse` (585). For a code of length ≥ 5, chars 0-2 are the base (202 or 203), char 3 is In, char 4 is Out, and the optional char 5 is Bull (0 or 2 means 50, 1 means 25). The client sets `OISystem.OISetIn/OISetOut` and `DataMap.onlineOIBull` (593-662). Plain `202` defaults to In 0, Out 0, Bull 2 (663-669). | int |
| `game_setting` | Passed to `gamesetting.GetGameSetting` (587). Type 303 reads `scorezone[]` into `cricketHiddenScoreZone`. Type 5 reads `types` and `legs` with `int.Parse`, which sets `GamePK.MatchType` and `VDartPKGameMaxRound` (as/gamesetting.cs:20-42). Forward the inviter's object verbatim. | ints for type 5 |
| `vpoint_bet` | Stake shown in the dialog. `int.Parse` if non-empty (697). The invitee must have that many vpoints (1128-1135). | int or empty |
| `match_id` | `int.Parse` (581). The gambling match id is used in the invitee's HTTP deposit. In VCrown it is compared as a string (553). | **int, required** |
| `isSteelBoard` | `int.Parse` (578). 0 shows "J01", otherwise "J02". | **int, required** |
| `isHomeDarts` | `int.Parse` (579). 1 shows "H", otherwise "C". | **int, required** |

Variant-A invites (§3.2) do not include `vpoint_bet`, `match_id`, `isSteelBoard` or `isHomeDarts`. **The server must fill in defaults** (`"0"`) or the invitee crashes at line 578. That this was the original server's behaviour is inferred.

Client behaviour:
- If `State != Connected` (inviting someone else, invited by someone else, or in a game), it immediately replies `response false` (712-715).
- If it is in a non-lobby scene and has declined before (`InviteCancelCount > 0`), it calls `inviteNo()` silently and sends no reply (543-547).
- VCrown: the client auto-accepts if the invite comes from `competitorID` with a matching `match_id`. Otherwise it replies false (549-566).
- League: it calls `inviteNo()` silently (567-571).
- Otherwise it sets `State = Invited`, shows a Yes/No popup, and starts a **10 s auto-decline** (709).
- **On Yes** (`inviteYes`, 1117-1168):
  - It checks coins and stake.
  - With a stake, it does the HTTP deposit first (`vpointBet.php` type=deposit, 1176-1181), then sends `response true` (1221-1229).
  - Without a stake, it immediately sets `State = InGame`, sends `response true`, and **starts the game locally without waiting for any server confirmation** (`CreateOnlineGame(isIAmInvited:true)`, 1157-1161).
- **On No or timeout:** `response false` (1295-1301).

### 4.9 `cancel_invite`: the inviter withdrew

as/onlineMenuSystem.cs:717-734

```json
{"type":"cancel_invite","id":"<inviter lobby id>"}
```

The client always hides the popup and cancels the auto-decline timer. If `State == Invited` and `OpponentPlayer.id == id`, it returns to `Connected`. It sends nothing back.

### 4.10 `refuse`: the invitee declined (sent to the inviter)

as/onlineMenuSystem.cs:735-749. No fields are read. It only acts when `State == Inviting`: it sets `State = Connected` and shows "<name> declined".

```json
{"type":"refuse"}
```

**Inferred:** send this to the inviter on `response false`, on the server-side invite timeout, when the invitee disconnects, and when the invitee is busy.

### 4.11 `game_start`: the invitee accepted (sent to the inviter)

as/onlineMenuSystem.cs:750-786. No fields are read.

```json
{"type":"game_start"}
```

- It always cancels any pending auto-decline. If `onlineOISetting.StakeVpoint_bet > 0`, it sets `Game.IsGambling`.
- It is ignored unless `State == Inviting`, so sending it to the invitee is harmless.
- It sets `State = InGame` and calls `CreateOnlineGame(OnlinePlayer.GameType, isIAmInvited:false)`. The game type is the inviter's own locally remembered type, so the server does not need to echo it. In VCrown the type is derived from the `gametype` string (760-782).

### 4.12 `hit`: an opponent's dart (relay)

as/onlineMenuSystem.cs:790-792. `area` is read at the **top level** with `int.Parse`, then pushed into `OnlineMessage.Recv`.

```json
{"type":"hit","area":"60"}
```

Forwarding the sender's line unchanged works, including its `time` (inferred). The game scene applies it when the current player is the `PlayerType.Online` one (as/OISystem.cs:1087-1113; as/gameSystem.cs:337-339; as/cricketSystem.cs:798-821; as/countUpSystem.cs:372). Until then it stays queued. The queue holds 10 entries.

### 4.13 `round_end`: the opponent ended their turn (relay)

as/onlineMenuSystem.cs:787-789. The client pushes 87 into `OnlineMessage.Recv`. For the Online player, a received 87 is the **only** thing that advances the turn: auto-advance after 3 darts is disabled for `PlayerType.Online` (as/OISystem.cs:1736, 1796), and 87 calls `updateWait()` (as/OISystem.cs:1486-1530). At game end, 87 also triggers `GameOver_wait`/`GameOverProcess` (as/OISystem.cs:1324-1343).

```json
{"type":"round_end"}
```

### 4.14 `round_start` (relay, ignored)

as/onlineMenuSystem.cs:807-808. It may be forwarded or dropped.

### 4.15 `talk` (relay)

as/onlineMenuSystem.cs:801-803. `number` is read at the top level with `int.Parse`, then passed to `OnlineConversationsSystem.instance.ReceiveOnlineConversations`. **If `OnlineConversationsSystem.instance` is null, for example in the lobby, this throws a NullReferenceException.** Only relay `talk` during a game.

```json
{"type":"talk","number":"3"}
```

### 4.16 `game_over`: the opponent left or finished

as/onlineMenuSystem.cs:793-800

```json
{"type":"game_over"}
```

It only has an effect when `!Online.isAroundEnd && State == InGame && SceneMenu.IsInGame()` (the CountUp, OI, Cricket or GameManager scenes; fp/SceneMenu.cs:70-77). In that case the client calls `CancelInvoke()` and sets `Game.Online_Robot = true`. On the next frame where the remote player is current:
- **Staked, VCrown or VCrownDouble games:** the opponent is marked `offLine = true`, the "VCrown_OnlineDisconnection" popup is shown, and the game ends immediately with the local player as winner (as/OISystem.cs:1096-1104, 1888-1903, 2875-2886; as/cricketSystem.cs:807-812).
- **Otherwise:** the opponent is replaced by a **robot** (`PlayerType.OnlineComputer`, rank = opponent rank or 12) and play continues locally (as/OISystem.cs:1105-1110, 1217-1323; as/gameSystem.cs:340-350).

`isAroundEnd` becomes true once the local game is in its end phase (as/OISystem.cs:1326-1329; as/gameSystem.cs:323-326; as/countUpSystem.cs:448). It is reset when the scene is destroyed (as/OISystem.cs:3221-3225; as/cricketSystem.cs:3081-3082; as/countUpSystem.cs:1360). A `game_over` relayed after normal completion is therefore ignored.

**This is the client's only opponent-disconnect signal.** Inferred server duty: when one participant sends `game_over` or `exit`, or its socket drops, send `{"type":"game_over"}` to the other participant.

### 4.17 `target_not_exist`

Consumed in the ReadThread (fp/Online.cs:399-403). It counts as an ACK for the outstanding message and sets `SelectedPlayer = null`. It does **not** change `State`. An inviter stays in `Inviting` with the "waiting…" dialog until the user presses Cancel.

```json
{"type":"target_not_exist"}
```

**Inferred:** the original server probably sent this, instead of `repeat`, when an invite, response or cancel_invite named an unknown id.

Do not follow `target_not_exist` with `refuse`. The `refuse` handler reads `SelectedPlayer.name` (as/onlineMenuSystem.cs:744). `target_not_exist` has already set `SelectedPlayer` to null, so the handler throws and the inviter stays in `Inviting`.

Recommendation:
- Unknown target on `invite`: send `repeat` and then `refuse`. Do not send `target_not_exist`.
- Unknown target on `cancel_invite` or `response`: send `repeat` only.

---

## 5. Full flow

### 5.1 Connect and log in
1. The user enters the OnlineMenu scene. `onlineMenuSystem.Init()` creates the persistent object (as/onlineMenuSystem.cs:84-97). `Start()` sets `auto=false` and `version="02.10"`, then calls `Online.Connect()` and starts the 30 s loading timeout (151-183).
2. The TCP connect succeeds and `State = Connected`. The read and write threads start, and `login` is queued (fp/Online.cs:222-258).
3. Server: send `repeat{time}`, then `login_success`, then `list` (excluding self). Then broadcast `add` for the newcomer to every other lobby client.

### 5.2 Lobby maintenance (server-pushed, unprompted)
- `add` on join or status change, and `remove` on leave or disconnect.
- `game_init` to onlookers when a game starts. When a game ends, send `add` with `bhv` not equal to `fgt`.
- **Heartbeat (inferred, strongly recommended):** the client computes `avgDelay = (avgDelay + secondsSince(lastRecvTime)) / 2` about every 1 s (fp/Online.cs:336-342). The in-game and lobby connection-quality icon shows full only when `avgDelay < 1.5 s`. It shows 0.8 below 2 s, then lower steps down to 0 at 3 s or more (as/ConnectionQuality.cs:61-91). So the server should send *something* at least every second. An **empty line** (`"\n"`) is the safest choice: it updates `lastRecvTime` and is otherwise ignored (fp/Online.cs:380-385). The client never sends a heartbeat. **Do not time out idle lobby clients aggressively**: a lobby client can stay silent indefinitely.

### 5.3 Invite
1. The inviter selects a player who is not `inGame` (as/onlineMenuEvent.cs:43-52), then sends `invite` (variant A or B) and sets `State = Inviting`.
2. Server: send `repeat` to the inviter.
   - If the target is unknown, see §4.17.
   - If the target is busy (in a game or with an invite pending): **inferred**, send `refuse` to the inviter. Otherwise the client would answer `response false` itself.
   - If the target is available: send the flattened `invite` (§4.8) to the target, and record a pending invite (inviter→target, game_type).
   - Start a server timeout of about 15 s (inferred). On expiry, send `refuse` to the inviter and `cancel_invite` to the target.
3. Inviter cancels: the client sends `cancel_invite{id:target}`. The server sends `repeat`, then `cancel_invite{id:inviter}` to the target, and drops the pending invite. The inviter has already returned to `Connected` locally (as/onlineMenuEvent.cs:204).
4. Invitee declines or times out: the client sends `response{id:inviter,response:"false"}`. The server sends `repeat`, then `refuse` to the inviter.
5. Invitee accepts: the client sends `response{...,"true"}`, sets **`State = InGame`, and loads the game scene immediately**. The server sends `repeat`, then `game_start` to the inviter, marks both players busy, and sends `game_init` to onlookers. If the invite is no longer pending, because of a cancel race, send `game_over` to the invitee (inferred). Its game then continues against a robot rather than hanging.

### 5.4 Game start: order, settings, randomness
- **Who throws first:** in `CreateOnlineGame` the invitee builds `[Local(self), Online(inviter)]` (as/onlineMenuSystem.cs:893-896) and the inviter builds `[Online(invitee), Local(self)]` (945-946). `Player[0]` starts (`currentPlayer = 0`, as/OISystem.cs:780). **The invitee always throws first.** No coin toss or server decision is involved.
- Max rounds: CountUp 8, 501/701 15, Cricket 15, Sniper 8, TicTacToe 12, CricketHidden 15, SuperV 15 (as/onlineMenuSystem.cs:855-892). The game scenes may override these with `GameParams.GetGameRound(Online_*)` (as/OISystem.cs:450-457; as/gameSystem.cs:203-216).
- **Settings are agreed only through the invite payload.** That covers 01 In/Out/Bull (game_type digits), CricketHidden zones (`game_setting.scorezone`, as/cricketSystem.cs:331-334), and Medley type and legs (`game_setting.types/legs`).
- **No random seed is exchanged.** Online TicTacToe uses a fixed `onlineavailablezone` table (as/gameTicTacToe.cs:36-41). Each client's simulation is deterministic given the relayed areas.
- The scene loads about 0.3 s later (as/onlineMenuSystem.cs:1363-1420). There is a 2 s `START_DELAY` at the start of each turn (as/GameParams.cs:20). The server does not need to wait for anything. The two clients have no ready handshake.

### 5.5 Per-dart relay and turn change
- The local thrower sends `hit{area}` per dart. The server sends `repeat` and relays `{"type":"hit","area":"..."}` to the opponent.
- At the end of the turn the thrower sends `round_end` and, as the next local turn begins on the other side, `round_start`. The server relays `round_end` to the opponent, which turns it into 87 and advances. Relaying `round_start` is optional.
- Local idle timer: **40 s** for the first idle, **15 s** after that (fp/GlobalData.cs:59-61). On expiry the client auto-ends the local turn and sends `round_end` (as/OISystem.cs:1036-1066; as/gameSystem.cs:542-571; as/cricketSystem.cs:760-781).
- **The receiving side has no timer for the remote player's turn.** If the remote client freezes, the local client waits forever unless the server sends `game_over`. **Inferred recommendation:** if no `hit` or `round_end` arrives from the current thrower for about 60 s, send `game_over` to the other player.
- Ordering is guaranteed by stop-and-wait. Each message is sent only after the previous one is ACKed, so the ACK latency limits dart throughput.

### 5.6 Game over and return to lobby
1. Both clients detect the end locally and independently. The thrower's final `round_end` drives the remote side's end (§4.13).
2. The GameOver scene sets `State = Connected` (as/GameOverSystem.cs:88-91).
3. The user presses OK: the client sends `game_over` and loads the Online scene (as/GameOverSystem.cs:123-140). The persistent `onlineMenuSystem` just redraws its cached list (as/initSystem.cs:149-154). **There is no re-login and no new `list` request.** The server should push `add` updates for both players (inferred).
4. Server: on the first `game_over` from a participant, relay `game_over` to the other one. It is ignored if that side is already in the end phase. Then mark both players idle and broadcast `add` with an idle `bhv`.
5. Medley (SuperV): between legs nothing goes on the wire (`GameOverSystem.KeyOK`, as/GameOverSystem.cs:106-122). Both clients go straight to the next leg. `game_over` is sent only after the last leg. The leg order and starting thrower are computed locally from previous leg winners (as/VDartsPKSystem.cs:330-361).
6. Rematch from the GameOver screen: the client sends `game_over` and then a variant-B `invite` (as/GameReMatch.cs:60-68; as/onlineOISetting.cs:285-293). After that, the normal invite flow applies.

### 5.7 Disconnects
- Client-initiated: `exit` followed by a socket close (§3.9).
- The client does not detect a server close (§0.2). It shows nothing and ignores the condition.
- **Inferred server duties:**
  - On a socket drop or `exit`: broadcast `remove{id}`.
  - Send `game_over` to the opponent if the player was in a game.
  - Send `refuse` to the inviter if the player had a pending incoming invite.
  - Send `cancel_invite` to the target if the player had a pending outgoing invite.

---

## 6. Timers summary

| Timer | Value | Side | Source |
|---|---|---|---|
| Resend un-ACKed message | 99.75 s | client | fp/Online.cs:350 |
| Login/list loading timeout | 30 s (OnlineMenu scene only) | client | as/onlineMenuSystem.cs:329 |
| Invite popup auto-decline | 10 s, then `response false` | invitee | as/onlineMenuSystem.cs:709; fp/GlobalData.cs:95 |
| Inviter waiting for answer | **none** | inviter | — |
| Local turn idle | 40 s first, 15 s later, then auto `round_end` | thrower | fp/GlobalData.cs:59-61 |
| Remote turn | **none** | receiver | — |
| Auto-advance after 3rd dart | about 0.5 s + effects + 1 s, then `round_end` | thrower | as/OISystem.cs:1839-1849; fp/GlobalData.cs:65 |
| Connection-quality sample | 1 s; icon refresh every 3 s | client | fp/Online.cs:336; as/ConnectionQuality.cs:63-68 |

---

## 7. HTTP calls made during the online flow (not part of the TCP protocol)

Base for `API.*`: `http://api.vdarts.net/MemberAPI` by default (as/VDartsGame.Assets.Script.Init.Network/APICore.cs:321-345). Abbreviated below as `API.cs` = as/VDartsGame.Assets.Script.Init.Network/API.cs.

| Call | Endpoint | Where |
|---|---|---|
| VPoint balance | GET `/vpointGet.php?token=<ascii token>` | API.cs:82-90; as/onlineMenuSystem.cs:104-126 |
| Stake amounts and fee | GET `/vpointBet.php?type=bet` (and `type=fee`), which reads `data.is_locked`, `data.fee`, `data.amount[]` | API.cs:348-355; as/onlineMenuSystem.cs:260-311 |
| Stake create (inviter) | POST `/vpointBet.php` with body `{"type":"invite","vpoint_bet":N,"token":"..."}`. It returns `data.match_id`, which is then used as the invite's `match_id`. | API.cs:366-373; as/onlineMenuSystem.cs:1170-1174, 1190-1216 |
| Stake deposit (invitee) | POST `/vpointBet.php` with body `{"type":"deposit","match_id":N,"token":"..."}`. On success the client sends `response true`. | as/onlineMenuSystem.cs:1176-1181, 1221-1229 |
| Stake settlement (winner) | POST `/vpointBet.php` with body `{"type":"settlement","match_id":N,"token":"...","winner":<member_id>}` | as/onlineMenuSystem.cs:1183-1188; as/GameOverType.cs:88-91 |
| Opponent rank and background | GET `http://api.vdarts.net:8000/MemberAPI/memberGet.php?id=<member_id>`, which reads `rank.average`, `consume_rank`, `fx.txt`, `fx.bak` | fp/OnlinePlayer.cs:71-106; fp/ServerIpControl.cs:260 |
| Opponent effects | GET `/fxPersonal.php?token=&member_id=` | API.cs:294-301; as/onlineMenuSystem.cs:1019-1056 |
| Opponent custom text | `.../VDartsAPIs/fx?memberid=`. **Dead code**: `GetOnlinePlayerCustomText` is empty. | as/onlineMenuSystem.cs:1347-1361 |
| VCrown match list and ladder | GET `/league3MatchList.php?type=&token=` (and `&match_id=`) | API.cs:375-391 |
| VCrown result | POST `/league3MatchList.php` with a JSON body | API.cs:393-400; as/onlineMenuSystem.cs:1248-1277 |
| VCrown end-of-match | `VCrownGamesData.VCrowndoubleUpdateEndMatch` | as/onlineMenuSystem.cs:205-233; as/VCrownGamesData.cs:86 |
| VPoint for playing | POST `/vpointAdd.php` with `token=&action=game&mac=&gameType=online` | API.cs:72-80; as/OISystem.cs:2810-2825 (same in as/cricketSystem.cs:2766 and as/gameSystem.cs:1596) |
| Token re-validation on login_fail | `PlayerInfoCtrl.CheckTokenVaild` | as/onlineMenuSystem.cs:366 |

---

## 8. Open questions and ambiguities

1. **Exact shape of server→client `invite`.** The client reads top-level fields, so the server rewrites the message. Whether the original server added `vpoint_bet`/`match_id`/`isSteelBoard`/`isHomeDarts` defaults for variant-A invites is unknown, but they are required for the client not to crash (as/onlineMenuSystem.cs:578-581).
2. The original server's **heartbeat** format and interval are unknown. An empty line is recommended.
3. **How busy is cleared** after a game is unknown. No dedicated message exists. `add` with `bhv` not equal to `"fgt"` is the only per-player mechanism. Valid non-`fgt` values for `bhv` are unknown.
4. **Is self in `list`?** No client filter exists. Excluding self is recommended.
5. Whether the server checks game_type, version or board compatibility is unknown. The client already enforces version gates on the inviter side.
6. Semantics of `auto:"true"` sessions (connections opened from inside a local game via `onlineMenuSystem.Init()` on the first hit, as/OISystem.cs:1771-1775): presumably they are listed so lobby users can see them or invite them. Their client is in an in-game scene, so it auto-declines with `response false` because `InviteCancelCount` and state checks apply.
7. `game_type` in `response` can differ from the invite's game_type, as in 202 vs 202012. Do not validate them against each other.
8. `league_id` is echoed in player entries and is only meaningful for VCrown pairing. It is the VCrown `match_id`, or `"0"`.

---

## 9. What vdarts-server implements (`Lobby.cs`)

**Listener**
- TCP on `Vdarts:LobbyPort` (default 8008; set 0 to disable).
- One reader and one writer per connection.
- Lines are UTF-8 and terminated with `\n`.
- An empty line is sent after each 1 s with no traffic (heartbeat).

**IDs and the ACK**
- Lobby ids are per-connection counters starting at 101. They are not member ids.
- Every client message that carries a `time` is ACKed with `repeat` before it is processed.
- A repeated `time` on the same connection is ACKed again but not processed again.

**`login`**
- The `token` is looked up the same way as for HTTP: the 128-hex "Java" form or the 64-char game_token. An unknown token gets `login_fail`; an empty one gets `login_without_token`.
- On success the server sends `login_success`, then `list` (without the player itself), then `add` to every other player.
- If the same member logs in on a new connection, the older session is dropped as if it had disconnected. This is needed because the client never notices a dead socket.

**Player objects**
- `logo` is `<member_id>.png`. The client prefixes its portrait URL, and the plugin redirects that URL to `/picture/portrait/`.
- `rank.average` is the same level string as `memberGet.php` (e.g. `B8`). The client strips the letters.
- `bhv` is `fgt` while the player is in a game and `idle` otherwise.

**`invite`**
- The server rewrites the invite with every field at the top level. `vpoint_bet`, `match_id`, `isSteelBoard` and `isHomeDarts` default to `"0"`, and `game_setting` is forwarded unchanged.
- The inviter gets `refuse` if the target is unknown, is the inviter, is in a game, or already has an invite pending.
- Sending a new invite cancels the sender's previous one. A rematch (`game_over` followed by `invite`) works.

**Invite outcomes**
- If there is no answer within 15 s, the inviter gets `refuse` and the target gets `cancel_invite`.
- `response true` starts the game: the inviter gets `game_start`, and everyone else gets `game_init`.
- `response true` for an invite that no longer exists gets `game_over`, so that client's already-started game continues against the robot.

**In-game relay**
- `hit`, `round_end`, `round_start` and `talk` are relayed only to the opponent, only while the pair is in a game, and only with the fields the receiver reads.

**End of game**
- The first `game_over` from either player ends the pairing:
  - the opponent gets `game_over`;
  - both players are announced with `add` and `bhv:"idle"`.
- If neither player sends `hit` or `round_end` for 75 s, both get `game_over`. The client's own idle timer forces a `round_end` within 40 s, so this only triggers when a client is frozen.

**Disconnects**
- A disconnect or `exit` ends any game the player is in, cancels its outgoing invite, and sends `refuse` to anyone waiting on it. Every other player gets `remove`.

**Not implemented**
- V-point stakes (`vpointBet.php` keeps `is_locked=1`, so staked games are hidden).
- VCrown/League pairing logic beyond echoing `league_id`.
- Webcam and microphone relay.

`vpointBet.php?type=bet` has to return at least one non-zero `amount`. If it does not, the lobby's 01/Cricket settings dialog throws a NullReferenceException in `onlineOISetting.OnlineInvitation` (`StakeVpoint_betArray` is never built), and the invite is never sent.
