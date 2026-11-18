# VDarts HTTP catalog: Java (`:8080/VDartsAPIs`) and member/card-login paths

Scope: fpfull/* (Game, GameKey, OnlinePlayer, PhotoObject, HttpObject, PlayerInfoPool, ServerIpControl) and full/ PlayerInfo, PagePlayerInfo, PlayerInfoUploader, PlayerLoginSystem, cardPlayerSystem, NewMenu2D (settings card swipe), NewMenu2D_Member, NewMenu2D_Register, Money, LockGame, GameOverSystem, DataPush, JsonRankings, onlineMenuSystem, VersionCheck, NetCheck, Reporter.
Paths are relative to `scratchpad/`. Client version string: `02.10.04` (sent as `021004` to PHP).

## 0. Things you need to know before implementing anything

**JSON parser (I2.Loc.SimpleJSON, `fpfull/I2.Loc.SimpleJSON`)**
- It stores every scalar as a string. Numbers, `true`, `false` and `null` all come back as strings: `.Value` of `true` is `"true"`, and of `null` is `"null"`. **Never send JSON `null`.** Send `""` or leave the key out.
- `node.ToString()` on a scalar returns it in quotes, e.g. `"\"abc\""`. Several checks rely on this (for example `["token"].ToString().Length < 9` means the token must be at least 7 characters).
- A missing key returns a "lazy" node. Comparing it with `== null` gives true, and both `.Value` and `ToString()` give `""`.
- `JSONClass.ToString()` writes `{"k":"v", ...}`. That matters wherever the code tests `.ToString().Length`.

**Unity `WWW` / `UnityWebRequest`**
- Any HTTP status other than 2xx sets `.error`, and most callers then treat the call as failed.
- `WWWForm` POSTs are `application/x-www-form-urlencoded` and URL-encoded. If the form holds binary data (`AddBinaryData`), the request is multipart instead.

**PHP API layer (`APICore`, in `full/VDartsGame.Assets.Script.Init.Network/APICore.cs`)**
- Card login and record upload go through this layer, not through `WWW`.
- The request is built by hand (`"k=" + v + "&..."`). **Nothing is URL-encoded.** It is sent as a POST with `Content-Type: application/x-www-form-urlencoded`, or as GET with `?data`.
- The host is chosen by `APIHost()` (APICore.cs:315):
  - `https://api.vdarts.net/MemberAPI`, port 443, with certificate validation turned off (`AcceptAllCertifications`), or
  - `http://<ip-or-api.vdarts.net>:8000/MemberAPI`, whichever has the lower average latency. While both averages are 0, https wins. The ":8000" host may be the IP address remembered from an earlier connection.
- `EchoTest` sends `GET /MemberAPI/echo.php` to both hosts. For id -1, it reads `"cip": "<ip>"` from the body to learn its own IP address (APICore.cs:404 and 297).
- **A non-200 response is treated as an empty result.** On a `WebException` the response body is never read and `result` becomes `""`. Always answer with HTTP 200 and put errors in the JSON.
- If the response `Content-Type` starts with `image`, the body is written to a file instead of being read as text (APICore.cs:162). Send JSON as `application/json` or `text/html`.

**Tokens (APICore.cs:461-485, StringExtensions.cs:16)**
- PHP login returns `game_token`, a 64-character ASCII string.
- `APICore.ToJavaToken()` hex-encodes it to 128 characters **only if its length is exactly 64**. That 128-character "Java token" goes into `PlayerInfoPool.tokens[]` and `Game.Player[i].token`.
- Later, `StringExtensions.HexToASCII(token)` turns it back into 64 characters for PHP calls (`fxPersonal.php`, `vpointGet.php`, `lottery.php`). It throws on input that is not hex.
- ⇒ **`game_token` must be exactly 64 characters.** Using 64 hex characters is safest.

**Unused constant:** `ServerIpControl.USER_SERVER_IP` (`:8080/MemberPlatformWS`) is never referenced. The only MemberPlatformWS URL in use is the hard-coded `PHP_API.FIND_MEMBER_ACCOUNT = http://api.vdarts.net:8080/MemberPlatformWS/card`, which the PHP_API agent covers.

**`ServerIpControl.ChangeControl()`** sets `MACHINE_MAC = Game.Mac`. `Game.Mac` is the NIC MAC address formatted as `XX-XX-XX-XX-XX-XX` (initSystem.cs:69-73). `PlayerInfoPool.uploadUrl` is `HOME_BOARD_SERVER_IP + "/record"`, but nothing ever POSTs to `/record` (see section 9).

---

## 1. MEMBER CARD LOGIN, end to end (top priority)

### 1.1 Where the card number is read (keyboard-wedge reader)

**`full/cardPlayerSystem.cs:290-372` (`Update`)** handles card login on the player-select screen:
- Each frame it appends `Input.inputString` and keeps only the digits `0-9`. If any other character arrives, the buffer is cleared (`flag`).
- When the buffer reaches 10 or more digits, it takes `Substring(0,10)` and plays the "Login" audio. Then:
  - If the card-to-coin panel is open, it calls `IE_cardToCoin` (→ PHP `consumeCardToCoin.php`; see 1.6).
  - Otherwise, if `Game.IsVTeame` is set, it calls `memberLogin(card, null, playerTeam)`.
  - Otherwise, if `playerKey < Game.MaxPlayer`, it calls `memberLogin(card, null, splayerloc)` (line 365). In SuperV it only accepts the pre-assigned cards and also fires `GameKey.Check` (§3).
- **Watchdog (cardPlayerSystem.cs:272-287):** if `loginState` stays true for more than **5 s**, the coroutine is stopped and `ShowOnlineError()` runs. **The whole login must finish within about 5 s.**

Other places that read cards:
- `full/NewMenu2D.cs:546-562`: a card swiped on the settings screen goes to `checkGameKey` (§1.5).
- `full/PageSetting.cs:199-237`: `GameKey.Check` only.
- `League_MainScreen.cs:446` and `League_SureenderSreen.cs:65`: these use `API.LoginCard` and belong to the league agent.
- `NewMenu2D_Setting_GameInformation.cs:79`: loads the CoinManagement scene; no HTTP here.
- `OISystem.cs:977`: a dev fast-game hack for hard-coded cards; no HTTP.

### 1.2 Request: card login → **PHP `login.php`** (not the Java API)

`cardPlayerSystem.memberLogin` (cardPlayerSystem.cs:815-892) → `API.LoginCard(card, Game.GetMachine())` (`full/VDartsGame.Assets.Script.Init.Network/API.cs:33-41`)

```
POST https://api.vdarts.net/MemberAPI/login.php     (or http://api.vdarts.net:8000/MemberAPI/login.php)
Content-Type: application/x-www-form-urlencoded   (NOT url-encoded by client)
body: card_no=<10 digits>&machine_mac=<XX-XX-XX-XX-XX-XX>&card_login=1&src=game&system=vdarts&version=021004
```

- Before sending, the client checks `GlobalData.BUSINESS_CONNECT_FEE_IS_EXPIRED`. If it is set, the client shows "NotConnectFee" and sends nothing. initSystem.cs:237 sets it from `API.GetMachineStatus` (business agent).
- A card already present in `memberCard[]` is ignored.
- The client polls `API.LoginCard` up to 100 × 0.1 s. The APIDat timeout is 8 s, but the 5 s watchdog wins.

**Result handling (cardPlayerSystem.cs:854-887):**

| Server returns | Client does |
|---|---|
| null or `""` (any non-200, timeout, connection failure) | `ShowOnlineError()` |
| text shorter than 10 characters | `ShowCardError()` ("CardLogin_txtNoCardID") |
| `error_msg` equal to `machine_mac error` (exact) | `ShowMachineError()` |
| `token` empty, or `token` shorter than 7 characters, or `card_login.is_locked == "true"` | `ShowCardError()` |
| anything else | `AddMember(res, ...)` |

### 1.3 Response fields used by `AddMember` (cardPlayerSystem.cs:1971-2199)

| JSON path | Use |
|---|---|
| `token` | PHP web token. Must not be empty, and `ToString()` (quoted) must be at least 9 characters. Stored in `PlayerInfoPool.webtokens[k]` and VTeam `member_token`. |
| `game_token` | **Exactly 64 characters.** `ToJavaToken` turns it into a 128-hex string stored in `PlayerInfoPool.tokens[k]` and `Game.Player.token`. That value is later used as the `members` value for Java `/consume`, as the `WWW-Authenticate` header, and (after `HexToASCII`) for fx, lottery and vpoint. `PlayerObject.Check_Way_Of_Login` requires `token.Length > 9` for card payment. |
| `card_type` | `"crd.mem"` = member card (balance shown). `"crd.spr"` = sparring card. Any other value shows vpoint 0. |
| `residue_sparring_times` | Read only when `card_type == "crd.spr"`. Parsed with int.TryParse into the sparring times/points. |
| `card_login` | The whole object is stored as `PlayerInfoPool.playerInfos[k]`. This is the **player profile**. Fields below. |
| `card_login.account` | Duplicate-login check (`PlayerInfoPool.loginedAccounts`), then `AddPlayerInfo(account, "******")`. **Must be unique per member and non-empty.** Two members with `""` would block each other ("Repeated Login"). |
| `card_login.member_id` | Read through `GetInfo(k, "member_id")` and stored in `Game.Player.id`. Later sent as `member_id` in game records. VTeam uses `ToString()`, which keeps the quotes. |
| `card_login.nickname` | Display name: `new PlayerObject(..., nickname)`. Also compared with `Game.Conpany_name` for sparring cards (`ToString()`, quoted). |
| `card_login.logo` | Absolute avatar URL fetched with `new WWW(logo)` (line 2172). `""` uses the default `p5`. A failed download also falls back to `p5`. |
| `card_login.nation` | 2-letter country code, used for `Resources.Load("Country/"+nation)` (e.g. `TW`, `CN`, `US`). |
| `card_login.is_locked` | `"true"` rejects the card. |
| `card_login.rank["2"]` | "01-game" rating, stored as `Game.Player.ratingOI`. Also `rankarg` in 01 or Freeze games. |
| `card_login.rank["3"]` | Cricket rating, stored as `Game.Player.ratingCR`. Also `rankarg` in Cricket games. |
| `card_login.rank["average"]` | `rankarg` in any other game type. Shown as "rank" on the login card. |
| `card_login.consume_rank` | Passed to `showPlayerData` as `ratname` (not displayed). Shown in the online lobby. |
| `card_login.balance` | Read only when `card_type == "crd.mem"`. If missing, or `ToString().Length < 10`, both vch and gft become 0. Otherwise `balance["pay.vch"]` → `Player.vch` and `balance["pay.gft"]` → `Player.gft` (int.TryParse). The displayed "vpoint" is vch + gft. Card payment is allowed when `vch` or `gft` ≥ the game's card price. |
| `card_login.vpoint` | VTeam only: `int.Parse` (throws if missing while VTeam fee > 0). |
| `card_login.fx` | Optional. When present: `fx.txt` holds custom text, keys `fx_.txt.bul / dbl / ltn / htn / whs / htk / ton / bak / bed`, or the legacy `bull / double_bull / low_ton / high_ton / white_horse / hat_trick / ton80 / three_in_the_black / three_in_a_bed`. `fx.bak`, `fx.dbg` and `fx.mbg` hold `{ "gam.crk":{"url":..}, "gam.01g":{"url":..}, "gam.cup":{"url":..} }` background URLs. `ToString()` is stored in `playerCustomstr` and parsed again in PlayerLoginSystem.cs:233. |
| `error_msg` | Only `machine_mac error` is checked. |

**Rating format.** Ratings are level strings, not PPD or MPR numbers. `GameParams.RankingStrToInt` (GameParams.cs:470) takes the first run of digits. League code defaults to `"N1"` and strips `[A-Z]`. `onlineMenuSystem.cs:899` does `float.Parse(Regex.Replace(rank.average, "[_a-zA-Z]", ""))`. **`rank.average` must contain digits** (e.g. `"8"` or `"A8"`), otherwise the online lobby throws. The client never receives PPD or MPR from the server; it computes them locally and only uploads them (§9).

### 1.4 Requests that follow a successful card login
1. `GET https://api.vdarts.net:8043/MemberAPI/fxPersonal.php?token=<HexToASCII(javaToken)>` (cardPlayerSystem.cs:2201). The fx/mall agent covers it.
   - The client reads `used[i].type / target / fx / addtion`.
   - Minimal response: `{"personal":[],"used":[]}`.
   - Not called for sparring cards.
2. `GET <card_login.logo>`: the avatar image (png or jpg).
3. Nothing else is called before the game. Payment happens at game start (§1.6).

### 1.5 Card swipe on the settings screen: `NewMenu2D.checkGameKey` (NewMenu2D.cs:655-725)

Steps:
1. `GameKey.Check(card)` (§3). If the card is a game key, the settings unlock.
2. Otherwise `Game.memberLogin` runs → **Java `/member/login`** (§2).
3. On success, these run in parallel:
   - PHP `memberToken.php`, then `gameRecordGetRanking2.php` four times, plus `MemberPlatformWS/card` (PHP_API agent);
   - **Java `/member`** (§2b) via `Game.GetPlayerInfo`.
4. The "user information" panel shows: `nickname`, `rank.average`, `nation`, `balance["pay.vch"]` (`"0"` when `balance` is missing or its `ToString()` is under 10 characters), `account`, and `logo`.
   - `logo` is downloaded with `UnityWebRequestTexture`.
   - ⚠️ If that download fails (including when `logo` is `""`), `m_isgamekeychecking` is never reset (NewMenu2D.cs:925-930). After that, card swipes on the settings screen are ignored until the scene reloads. **Always return a reachable image URL in `logo`.**

### 1.6 Payment calls tied to a logged-in card

**Java `/consume`** is described in §5.

`CoinController.cs:150` → `API.MemberPay`:
```
GET /MemberAPI/consume.php?data=[{"type":"pay.mic","mac":..,"member_id":..,"game":0,"coin":N},...]
```
- Not URL-encoded. The response is ignored.

`cardPlayerSystem.IE_cardToCoin` (line 765) → `API.consumeCardToCoin`:
```
POST /MemberAPI/consumeCardToCoin.php
body: card_no=..&machine_mac=..&value=N
```
- Response `{"success":"1"}` inserts `value` coins.
- `{"error":"shop"}` and `{"error":"pay.vch"}` show specific messages. Anything else shows `errNetData`.
- A response of 8 characters or fewer shows the online error.

### 1.7 Minimal successful card-login response (login.php)
```json
{
  "token": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "game_token": "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
  "card_type": "crd.mem",
  "card_login": {
    "account": "card_0012345678",
    "member_id": "1001",
    "nickname": "Player1",
    "logo": "http://api.vdarts.net:8080/picture/portrait/1001.png",
    "nation": "TW",
    "is_locked": "false",
    "vpoint": "500",
    "consume_rank": "1",
    "rank": { "2": "8", "3": "8", "average": "8" },
    "balance": { "pay.vch": "500", "pay.gft": "0" },
    "fx": { "txt": {}, "bak": {} }
  }
}
```
Sparring card: `"card_type":"crd.spr","residue_sparring_times":"10"`, and `card_login.nickname` must equal the shop's `Game.Conpany_name` **including the JSON quotes**, because `ToString()` is compared. In practice this check only passes when `Conpany_name` is empty.
Errors: `{"error_msg":"machine_mac error"}` (HTTP 200), or `{"token":""}` for an unknown card.

---

## 2. Java `POST /member/login`

`http://api.vdarts.net:8080/VDartsAPIs/member/login`

**Callers**
- **(a) Card:** `fpfull/Game.cs:152-200` `Game.memberLogin` (used by NewMenu2D.cs:675). WWWForm `card_no=<10 digits>`, URL-encoded.
- **(b) Account (legacy UI):** `full/PlayerInfo.cs:91`. Form `account=..&password=..`, sent as raw `WWWForm.data` bytes (urlencoded).
- `PHP_API.test_MemberLogin` is a duplicate test path.

**Parsing in (a) Game.cs**

| Condition | Result |
|---|---|
| `.error` set and `error.Length >= 16`, starting `"400"` or `"401"` | `OnShowCardError(false)` |
| any other `.error` | `OnShowOnlineError()` |
| otherwise, `token` empty or `isValid.Value != "true"` (exact, lowercase) | card error |
| otherwise | `PopupSystem.DisplayLoginEffect()`, then `OnLoginSuccess(JSON, card)` |

Note: Unity's error for 400 is `"400 Bad Request"`, which is 15 characters, so a 400 falls into "online error" rather than "card error".

After success, NewMenu2D reads only `loginWWW["token"]`. It passes that token to PHP `memberToken.php?token=` and to Java `/member` (header).

**Parsing in (b) PlayerInfo.cs**
- `isValid` must not be `"false"` or `""`.
- `token` is saved to `LoginData` and then used in `GetMemberData(token)`.

**Minimal response**
```json
{ "isValid": true, "token": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }
```
Failure: HTTP 200 with `{"isValid":false,"token":""}`. That gives a clean "card error". A 401 also works, but only because "401 Unauthorized" is 16 characters.

## 2b. Java `GET /member`

`http://api.vdarts.net:8080/VDartsAPIs/member[?mac=<MACHINE_MAC>&card=<card>]`
Header `WWW-Authenticate: <token>`. Callers send either the token from `/member/login` or `PlayerInfoPool.tokens[i]`.

**Callers**
- `fpfull/Game.cs:215-230` `Game.GetPlayerInfo`: with `?mac=&card=`.
- `PHP_API.GetPlayerInfo` (PHP_API.cs:147): the same request.
- `full/PlayerInfo.cs:183` `GetMemberData`: no query string.
- `full/PlayerInfo.cs:248` `RefreshMemberData`: no query string.

**Parsing**
- Game.cs: fails (online error) if the text is null or shorter than 10 characters, or if `member_id` is empty. On success the whole object goes to the callback.
- PlayerInfo.cs: `member_id != ""` is required. The JSON is stored as `PlayerInfoPool.playerInfos[n]`, so the same member object fields as §1.3 apply.
- Fields read:
  - `nickname`, `rank.average`, `nation`, `balance["pay.vch"]`, `account`, `logo` (NewMenu2D.cs:796-815);
  - `vcoin`, `rank["2"]`, `rank["3"]`, `consume_rank` (PlayerInfo.cs:337 text dump);
  - `logo` is downloaded.

**Minimal response** (the same shape as `card_login`)
```json
{ "member_id":"1001","account":"card_0012345678","nickname":"Player1","nation":"TW",
  "logo":"http://api.vdarts.net:8080/picture/portrait/1001.png","vcoin":"500",
  "rank":{"2":"8","3":"8","average":"8"},"consume_rank":"1",
  "balance":{"pay.vch":"500","pay.gft":"0"} }
```

---

## 3. Java `GET /is_gamekey` (operator / settings card)

`http://api.vdarts.net:8080/VDartsAPIs/is_gamekey?mac=<Game.Mac>&card=<10 digits>` (not encoded; no special characters anyway)

**Callers**
- `fpfull/GameKey.cs:31-93` `GameKey.Check`
- NewMenu2D.cs:664, PageSetting.cs:219, cardPlayerSystem.cs:350 (SuperV)

**Local short-circuit (no request is sent)**
- The hard-coded staff cards `2513081404` and `1762817609`.
- The locally saved "SettingGameKey" (the first entry).

**Behaviour**
- If the card is in the local `GameKeyPool`, `afterTrue` runs immediately and the request is still sent.
- `JSON.Parse(text)["result"].Value == "1"` counts as a game key: the card is added to the pool and `afterTrue` runs.
- Any other value, or a parse exception, removes the card from the pool and runs `afterFalse`. On the settings screen, `afterFalse` goes on to the member lookup (§1.5).

**Responses**
- `{"result":"1"}`: the card is a game key and unlocks settings.
- `{"result":"0"}`: not a game key (a normal member card).

---

## 4. Java `GET /version`

`http://api.vdarts.net:8080/VDartsAPIs/version?machine_type=1&version_type=now`. Caller: `full/VersionCheck.cs:43-65`. It runs once at startup, only after `NetCheck` reports online.

**Response: plain text**, e.g. `02.10.04`. The client removes the dots, pads right to 5 characters, and runs `int.Parse`.
- Server value ≤ 21004 (the local version): nothing happens.
- Server value greater: a popup appears and `Application.OpenURL(BIG/version/<ver>)` is called.
- Non-numeric text throws inside the coroutine, which is harmless.

**Minimal response:** `02.10.04` (or an empty 200 body).

## 4b. NetCheck (`full/NetCheck.cs`)

The client sends `GET http://www.vdarts.net` from a background thread every 15 s. A 2xx response sets `isOnline = true`. That gates VersionCheck. **`www.vdarts.net` must resolve and answer with a 2xx.**

---

## 5. Java `POST /consume` (member card pays for the game)

`http://api.vdarts.net:8080/VDartsAPIs/consume`
WWWForm, URL-encoded: `game=<GameType int>`, `members=<java token>[,<java token>...]` (128-hex tokens, comma-separated), `mac=<Game.Mac>`.

**Callers**
- `full/Money.cs:104-118` (fire-and-forget). Called from `CoinController.cs:156` for normal games when a member has enough `gft`/`vch`.
- `full/Money.cs:120-137` (coroutine). Called from `cardPlayerSystem.cs:1163` for All-You-Can-Play.

**Parsing**
- Only `www.error` is checked: 2xx means success. The text is logged but not parsed.
- The fire-and-forget version reads `.error` immediately (always empty at that point) and sets the online indicator.

**Minimal response:** HTTP 200 `{"result":"1"}` (any body). The server should deduct the price from the member's `pay.gft` first, then `pay.vch`. The client deducts locally in the same order, gft before vch (CoinController.cs:86-107).

`Money.SparringCardConsume` (Money.cs:139) builds a form but **never sends it**.

## 6. Java `POST /coin` (offline-lock release probe)

`http://api.vdarts.net:8080/VDartsAPIs/coin`, form `insert=0&consume=0&mac=<mac>`. Caller: `full/LockGame.cs:38-63`. It runs when the machine is locked for being offline too long. It retries every 300 s until it gets a 2xx, then unlocks the keyboard. **The body is ignored.**
Minimal response: HTTP 200 `{}`.
Normal coin sync goes to PHP `machineCheckCoin.php` (`Money.MoneyCheck` → `API.UploadConsumeCoin`). That response is `{"insert":"N","consume":"M"}`, both parsed with ulong.TryParse.

## 7. Java `GET /bundle` and `GET /record/bundle` (PK group id)

1. `http://api.vdarts.net:8080/VDartsAPIs/record/bundle` (`PlayerInfoPool.uploadUrl + "/bundle"`). Caller: `full/DataPush.cs:287-297`, called from `VDartsPKSystem.cs:72`.
2. `http://api.vdarts.net:8080/VDartsAPIs/bundle`. Caller: `full/PlayerInfoUploader.cs:64-74`. It appears unused because its result is discarded.

- Header `WWW-Authenticate: PlayerInfoPool.tokens[0]`.
- The response is **plain text parsed with `int.Parse`**. It becomes `GamePK.group_id`, which is later posted as `group_id`. Non-numeric text throws, and `group_id` keeps its previous value.

**Minimal response:** `1` (text/plain). Ideally a fresh integer for each PK match.

## 8. Java `GET /gameRanking` (leaderboards)

`http://api.vdarts.net:8080/VDartsAPIs/gameRanking?game=<code>`

**Caller:** `full/JsonRankings.cs:82-127` (started from RankingsSystem.cs:32). It runs once for each of 8 codes. Codes that failed are retried after `RANKINGS_RESEND_TIME` (60 s).

| code | slot | meaning |
|---|---|---|
| 202 | 0 | 501 |
| 208 | 1 | Online 501 |
| 301 | 2 | Cricket |
| 308 | 3 | Online Cricket |
| 101 | 4 | CountUp |
| 102 | 5 | Online CountUp |
| 603 | 6 | Sniper |
| 604 | 7 | Online Sniper |

**Response: a top-level JSON ARRAY, at most 100 entries.** `RankingsList` is `[8,100]`, so a 101st entry throws.

Each entry has these fields, all used as strings:
- `rank`: shown text
- `nickname`
- `nation`: country code; `""` hides the flag
- `score`: shown text
- `dealer`: shop name; names containing "利穎" or "VDartsTech" are blanked
- `time`: read from entry [0] only, then `DateTime.Parse`d for the "last updated" date. A bad format just blanks the label.

Rows past `count` are padded with empty rows. If the request fails, the slot is left as null rows, and the rankings UI throws on null `RankingsList` entries. So always return 200 with `[]` or with entries.

```json
[ {"rank":"1","nickname":"Player1","nation":"TW","score":"8.52","dealer":"My Bar","time":"2026-11-17 00:00:00"},
  {"rank":"2","nickname":"Player2","nation":"CN","score":"7.90","dealer":"","time":"2026-11-17 00:00:00"} ]
```

**Daily, weekly and monthly rankings (`DailyRankings*.cs`) are local only.** They are JSON files under `persistentDataPath`. There is no HTTP. Per-member rank on the settings panel comes from PHP `gameRecordGetRanking2.php`, which the PHP agent covers.

## 9. Game record upload ("/record")

**`PlayerInfoPool.uploadUrl` (`.../VDartsAPIs/record`) is never POSTed to.**
- `DataPush.Upload` (full/DataPush.cs:239-280) builds a WWWForm (`group_id`, `game_type`, `records`, `league_id`, `league_order`, `league_leg`) and then discards it.
- The real upload is the PHP call `API.UploadGameRecord` (API.cs:61-70):

```
POST https://api.vdarts.net/MemberAPI/gameVdartsRecord.php   (or :8000 http)
body (NOT url-encoded): game_type=<GameType int>&group_by=<1 single | 3 team/freeze>&records=[<rec>,...]
      &league_id=<id or "">&league_order=<n>&leagueor_leg=<n>&winner=<1-based PlayerWinnerId+1>
```
- Note the typo `leagueor_leg`.
- retry=9, timeout 30 s. **The response is ignored.**
- No auth token is sent. Records are identified only by `member_id`.

**Callers:** gameSystem.cs:1609-1634 (01), OISystem.cs:2398-2451, cricketSystem.cs:2587-2630, countUpSystem.cs:985-1005, gameCricketCountUp.cs:509-526. Upload happens only if at least one player has a token (DataPush.cs:130-141) and the game is not against a robot.

**Each `<rec>`** is written by MiniJSON (DataPush.cs:164-237):
```json
{"member_id":"1001","machine_mac":"00-30-18-04-10-DB","effect":{"Bull":3,"Ton80":1},
 "rating":23.45,"score":501,"record":[[d1,d2,d3],[...],...],"handicap_score":0,"version":"02.10.04"}
```
- `rating` is PPD (`getPPD`) for 01 and CountUp games, and MPR (`getMPR`) for cricket games.
- `record` has MaxRound rows of 3 dart-position codes.
- `effect` keys appear only when greater than 0: Bull, DBull, HatTrick, LowTon, HighTon, ThreeInABed, ThreeInTheBlack, Ton80, WhiteHorse, Mark5 … Mark9.
- Guest or non-member slots are uploaded with `member_id "9"`. Sparring-card players are also `"9"` in cricket.
- In online games `game_type` is forced to 102 (CountUp) or 308 (Cricket). In PK games it is `GamePK.Type`.

Minimal response: HTTP 200 `{"result":"1"}`. Because the body is not URL-encoded, the server must split `records=` on the raw `&` characters. MiniJSON output contains no `&`.

## 10. Avatar upload: `PICUPLOAD_IP`

```
POST http://api.vdarts.net:8080/VDartsAPIs/member/logo
```
- Caller: `full/PagePlayerInfo.cs:44-92` (legacy UI). Multipart form: `photo` = `head.jpg` (from `persistentDataPath/logo.jpg`, labelled `image/png`), and `id` = `member_id`. Header `WWW-Authenticate: PlayerInfoPool.tokens[cur]`.
- The response field `filepath` is read but not used.
- Minimal response: `{"filepath":"http://.../portrait/1001.jpg"}`.
- `PagePlayerInfo.DownloadPicture` (line 104) is a plain `HttpWebRequest` GET of any picture URL. It needs `Content-Length`.

## 11. Online-lobby opponent profile: `ONLINE_BACKGROUND` (PHP)

```
GET http://api.vdarts.net:8000/MemberAPI/memberGet.php?id=<member_id>
```
Caller: `fpfull/OnlinePlayer.cs:61-96`, from onlineMenuSystem.cs:400 and 467. Opponents come from the TCP lobby `online.vdarts.net:8008`; see `fpfull/Online.cs:234`. That socket protocol is out of scope.

Fields read when `.error` is empty:
- `rank.average`: stored in `player.rank`.
- `consume_rank`.
- `fx`: if it is not empty, `fx.txt` is stored as `CustomText`, and if `fx.bak` is present the whole `fx` object is stored as `CustomBG`.

Minimal response: `{"rank":{"average":"8"},"consume_rank":"1","fx":{}}`

## 12. Portraits / static images

- `ONLINE_PlAYER_PORTRAIT = http://api.vdarts.net:8080/picture/portrait/` + `logo` from the lobby, when `logo` does not start with "http" (OnlinePlayer.cs:41-59).
- `PhotoObject` (fpfull/PhotoObject.cs) does `new WWW(url)` and `LoadImageIntoTexture`.
- `HttpObject` (fpfull/HttpObject.cs) is a generic `new WWW(url)` wrapper with no callers.
- Every `logo` URL from a login is fetched with GET and must return png or jpg.
- GameOverSystem lottery prize images: `https://api.vdarts.net:8043/<image_path><image>`.

## 13. Account registration (`Register_IP`, PHP)

```
POST https://api.vdarts.net:8043/MemberAPI/signup.php
```
- Caller: `full/NewMenu2D_Register.cs:207-288`.
- WWWForm (encoded): `address=vdarts`, `phone=000`, `gender=male|female`, `account`, `realname`, `nickname`, `birthday=Y-M-D`, `password`, `nation`, `email`.
- A 2xx response counts as success, and the body is ignored.
- Only when `.error` is set does the client parse `error`: `"01"` = account taken, `"02"` = nickname taken, `"03"` = email taken. So return HTTP 4xx with `{"error":"01"}` for these, or 2xx `{}` for success.

## 14. Member account login (PHP, for reference)

`NewMenu2D_Member.MemberLogin` (NewMenu2D_Member.cs:226-357) → `API.LoginMember`:
```
POST /MemberAPI/login.php  account=..&password=..&member_login=1&src=game&version=021004
```
Fields used:
- `error_msg` ("email_not_active" or "email_forbidden" are errors).
- `member_login.is_locked`: must be **non-empty** and not `"true"`, so send `"false"` explicitly.
- `game_token` (64 characters) and `uuid`.
- `member_login` is used as the profile object, with the same fields as `card_login`, including `fx`.

It then calls fxPersonal and `VersionVPoint_IP` (`https://t1.vdarts.net/MemberAPI/versionVpoint.php`, form `token / platform / version`, whose response is ignored), and downloads the logo.

QR login uses `API.LoginGameToken` (`login.php?game_token=..&machine_mac=..`) and returns the same shape as card login. The PHP agent covers it.

## 15. Lottery after a game (GameOverSystem, PHP)

`full/GameOverSystem.cs:442-509` → `API.GetLottery`:
```
POST /MemberAPI/lottery.php  mac=<mac>&token=["<php token>",...]   (not encoded)
```
- If `error` is empty, the client reads `list[i].item_name` (the string `"null"` or empty means no prize), `list[i].image`, top-level `image_path`, `list[i].token`, `list[i].type` (AsInt), and `list[i].prize_level_id` (int.Parse).
- Minimal "no prizes" response: `{"error":"","image_path":"","list":[]}`. Or return `{"error":"none"}` to skip.
- Dead code path (`formalLotty` is true): the client would POST to the hard-coded `t1.vdarts.tw/MemberAPI/lottery.php`.

## 16. Other HTTP in scope (non-network or not ours)

- `Reporter.cs:1848`: `file://.../StreamingAssets/build_info.txt` (local).
- `onlineMenuSystem.cs`: these calls belong to the fx/gambling agents.
  - `vpointGet.php?token=` (response `{"vpoint":"N"}`, int.Parse);
  - `vpointBet.php`, `fxPersonal.php?token=&member_id=`, gambling and VCrown PHP calls;
  - `CustomText_IP` (`:8080/VDartsAPIs/fx?memberid=`), which expects `{"text":{bull, double_bull, ...}}`.
- Sockets (out of scope, noted only):
  - online lobby TCP `online.vdarts.net:8008` (JSON lines: login, invite, hit, ...);
  - `microphone.vdarts.net`, `webcam.vdarts.net` (MicrophoneServer and WebCameraServer).
- `LockGame` business mode polls `https://www.vdarts.net` (2xx means online) and pings `223.5.5.5` / `114.114.114.114`.
