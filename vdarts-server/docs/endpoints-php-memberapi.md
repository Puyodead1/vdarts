# VDarts PHP MemberAPI catalog (API.cs / IE_API.cs / LAN.cs / EfxDownloader.cs / PHP_API.cs)

Paths are relative to `full/` (Assembly-CSharp) unless prefixed `fpfull/` (firstpass).

## 0. Transport and parser facts (read first)

### APICore (base URL, encoding, polling)
- **Base URL.** APICore picks the lower-latency of two bases:
  - `https://api.vdarts.net/MemberAPI` (id -1/1)
  - `http://<resolved ip>:8000/MemberAPI` (id 2)
  - `is_java=1` switches to `http://api.vdarts.net:8080/VDartsAPIs` instead.
  - A url argument that starts with `http` is used as-is.
  - **The server must serve the same API on https:443 AND http:8000** (or DNS/hosts-redirect both).
- **POST body.** `application/x-www-form-urlencoded`, UTF-8, built by plain string concatenation `k=v&k2=v2`.
  - **Nothing is URL-encoded.** Raw JSON, `[`, `"`, `{`, spaces, `+` all arrive literally, so parse bodies leniently (split on the first `=` of each `&` chunk; JSON values may themselves contain `&` only in theory).
  - Only exception: `jsonData` in SuperV upload uses `Uri.EscapeDataString`.
- **GET.** `url + "?" + data`, also unencoded.
- **TLS.** Every certificate is accepted (cert validation callback returns true), so a self-signed certificate works.
- **Polling.** `APICore.Request` polls:
  - The first call starts the request and returns null.
  - Later identical calls return the body when it is done.
  - A failure or timeout returns `""`.
  - Default timeout is 15 s.
- **Retry bug.** `Retry()` re-issues without the `?query` and always as a body-writing request. Not important if the server answers first time.
- **Image downloads.** A response whose Content-Type starts with `image` is saved to a file. Image downloads MUST send `Content-Type: image/jpeg|png`.

### SimpleJSON (I2.Loc.SimpleJSON, old version)
Parser quirks a server author must know:
1. **Everything is a string internally.** `123`, `"123"`, `true` and `"true"` all read the same, and `null` becomes the string `"null"`. Numbers and strings are interchangeable, so quoting every value is safest.
2. **An empty string value is dropped.** `"logo":""` means the key behaves as missing:
   - `.Value` is `""`
   - `==null` is true
   - `AsInt` is 0
   - the implicit `(string)` conversion is **null**
3. **`node.ToString()` returns the value wrapped in quotes.** The game often compares with `"\"invalid mac\""` or tests `ToString().Length`. Lengths below therefore include the 2 quote chars.
4. **`JSON.Parse("")` returns null.** A top level array `[...]` is allowed.

### Tokens: the critical requirement
Login returns two tokens:
- `token` is the web token. It is stored in `PlayerInfoPool.webtokens[k]` and is basically only length-checked.
- `game_token` goes through `APICore.ToJavaToken(game_token)`:
  - If its length is exactly 64, it is hex-encoded ASCII (128 chars).
  - Otherwise it is returned unchanged.
  - The result is stored in `PlayerInfoPool.tokens[k]` and `Game.Player.token`.

Later calls do `StringExtensions.HexToASCII(PlayerInfoPool.tokens[k])` to get the token they send.
- **So `game_token` MUST be exactly 64 characters.** Use 64 lowercase hex chars. Any other length gives a non-hex or odd-length string, and HexToASCII throws or produces garbage.
- PHP endpoints receive the **original 64-char game_token** as `token=`.
- `vpointAdd.php` is the exception: it receives the 128-char hex form. The server should accept both forms; hex-decode it if the length is 128.

### Identity strings
- `Game.Mac` is the first Ethernet NIC MAC: uppercase, dash separated, e.g. `00-30-18-04-10-DB`.
- `version` is the game version without dots, e.g. `021004`.

---

## 1. POST /login.php with card_login=1 (API.LoginCard): TOP PRIORITY

**Request** (POST, timeout 8 s):
```
card_no=<card number>&machine_mac=<Game.Mac>&card_login=1&src=game&system=vdarts&version=021004
```

**Callers:**
- `cardPlayerSystem.cs:841` `memberLogin()`: polls `LoginCard` every 0.1 s, up to 100 tries.
- `League_MainScreen.cs:534`
- `League_SureenderSreen.cs:103`
- The rematch/auto-relogin path also goes through `memberLogin`.

### Decision logic (`cardPlayerSystem.cs:815-892`, then `AddMember` at 1971-2199)

| Condition | Result |
|---|---|
| Response null / "" (timeout) | ShowOnlineError |
| `text.Length < 10` | ShowCardError |
| `error_msg.ToString() == "\"machine_mac error\""` | ShowMachineError (machine not registered) |
| `token.Value` empty, or `token.ToString().Length < 9` (i.e. the token value is shorter than 7 chars) | ShowCardError |
| `card_login["is_locked"].Value == "true"` | ShowCardError (locked card) |
| `card_login.account` already in `loginedAccounts` (not `crd.spr`) | "Repeated Login" |
| Machine `BUSINESS_CONNECT_FEE_IS_EXPIRED` (machineStatus server_time > lock_after) | Card login blocked before the request |
| otherwise | AddMember |

On failure the server can return `{"error_msg":"card_no error"}`. The League screen compares `error_msg` with `card_no error`, and any response missing `token` becomes ShowCardError.

### AddMember: fields consumed
Top level:
- `token` (string, at least 7 chars): goes to `webtokens[k]`; `Check_Way_Of_Login` also requires `token.Length > 9`, **so use at least 10 chars, e.g. 64 hex.**
- `game_token` (**exactly 64 chars**): goes to `tokens[k] = ToJavaToken(game_token)`.
- `card_type` takes one of three values:
  - `crd.mem` (member card, uses balance)
  - `crd.spr` (sparring card: `Game.Conpany_name` from machineStatus `company_name.ToString()` must equal `card_login.nickname.ToString()`, and it uses top-level `residue_sparring_times`)
  - anything else, which gives vch = gft = 0
- `residue_sparring_times` (crd.spr only)
- `card_login` (object).

`card_login` fields:

| Field | Use |
|---|---|
| `account` | `PlayerInfoPool.AddPlayerInfo(account,"******")`, duplicate-login key |
| `member_id` | player id; VTeam stores `member_id.ToString()` (quoted) |
| `nickname` | displayed name; VTeam compares it; crd.spr compares it to the company name |
| `logo` | if non-empty, the game downloads it with WWW (URL). Omit it or use `""` |
| `nation` | flag code, e.g. `TW` |
| `is_locked` | `"true"` rejects the card |
| `vpoint` | `int.Parse` in VTeam fee check: **must be an integer string** |
| `consume_rank` | display only |
| `rank` | object (see below) |
| `balance` | object (see below) |
| `fx` | object (see below); `fx.ToString()` is stored in `playerCustomstr[k]` |
| `vcoin`, `text.*` | read by `GetInfo` helpers, optional |

`rank` object:
- `"2"` is the 01 rating; it becomes `ratingOI` and the rankarg for 01 games.
- `"3"` is the cricket rating; it becomes `ratingCR` and the rankarg for cricket games.
- `average` is the rankarg for other games.
- `league.average` is used by League.
- Format: a letter class followed by a number, e.g. `"B10.76"`, `"AA21.78"`, `"N1"`. `RankingStrToInt` takes the first run of digits, and League strips `[A-Z]` and parses a float. **The digits must be present.**

`balance` object (only when `card_type=="crd.mem"`):
- If `balance` is missing or `balance.ToString().Length < 10`, then vch = gft = 0.
- Otherwise `int.Parse(balance["pay.vch"])` and `int.Parse(balance["pay.gft"])`.
- `Check_Way_Of_Login` requires gft or vch to be at least the game price from gamePriceGet; otherwise "not enough credit". **Give a generous balance.**

`fx` object (all optional):
- `txt`: `{"fx_.txt.bul":..., "fx_.txt.dbl", "fx_.txt.ltn", "fx_.txt.htn", "fx_.txt.whs", "fx_.txt.htk", "fx_.txt.ton", "fx_.txt.bak", "fx_.txt.bed"}`, used for custom award texts (`PlayerLoginSystem.cs:233`).
- `bak`, `dbg`, `mbg`: `{"gam.01g":{"url":..},"gam.crk":{..},"gam.cup":{..}}`.
- Empty objects are fine.

### Follow-ups after login
- `GetPlayerFx`: a WWW GET to `https://api.vdarts.net:8043/MemberAPI/fxPersonal.php?token=<game_token>`, reading `used[]`. **Note port 8043**, hardcoded in `fpfull/ServerIpControl`. Return `{"personal":[],"used":[]}`.
- The logo download happens only if `logo` is non-empty.

### Minimal sample response
```json
{
  "token": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "game_token": "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
  "card_type": "crd.mem",
  "card_login": {
    "account": "card0001",
    "member_id": "1001",
    "nickname": "Player1",
    "nation": "TW",
    "is_locked": "false",
    "vpoint": "0",
    "vcoin": "0",
    "consume_rank": "1",
    "rank": {"2": "N1", "3": "N1", "average": "N1", "league": {"average": "N1"}},
    "balance": {"pay.vch": "9999", "pay.gft": "9999"},
    "fx": {"txt": {}, "bak": {}, "dbg": {}, "mbg": {}}
  }
}
```
Failure responses:
- `{"error_msg":"card_no error"}` for an unknown card.
- `{"error_msg":"machine_mac error"}` for an unregistered machine.

Generate a distinct `account` and `member_id` per card. Duplicate accounts on the same machine are refused.

## 2. POST /login.php with game_token (QR login: API.LoginGameToken)
- **Request:** `game_token=<32 hex GUID generated by the client>&machine_mac=<mac>&src=game&system=vdarts&version=021004`.
- **Flow:** the client shows a QR containing the GUID and polls this endpoint repeatedly until `token.Value.Length > 9`. The phone app binds the GUID to the account server-side. Until then, return `{}` or `{"error_msg":"wait"}`.
- **Response:** same as card login (`token`, `game_token` of 64 chars, `card_type`, `card_login{...}`).
- **Callers:** `cardPlayerSystem.cs:1840-1892`, `NewMenu2D_Member.cs:470-522`.

## 3. POST /login.php with member_login=1 (API.LoginMember: account/password)
- **Request:** `account=<raw>&password=<raw>&member_login=1&src=game&version=021004`. Values are **not** encoded.
- **Callers:** `NewMenu2D_Member.cs:243`; legacy `PlayerInfo.cs:125` reads `isValid`.
- **Checks:**
  - `error_msg` `"email_not_active"` / `"email_forbidden"` show specific messages.
  - `member_login.is_locked` must be non-empty and not `"true"`.
- **Uses:** `token`, `game_token` (64 chars), `uuid` (used in the cancelAccount sign), and `member_login{}` (same field set as `card_login`).
- **Sample:** same as section 1 with key `member_login` instead of `card_login`, plus `"uuid":"<any>"` and `"isValid":"1"`.

## 4. GET /machineStatus.php
- **Request:** `?mac=<Game.Mac>[&client_ip=<MyIP>]`. The business-register variant is `?machine_name=<n>&dealer_psw=<p>`.
- **Callers:**
  - `initSystem.cs:212` and `:303` (company_name)
  - `NewMenu2D_BusinessConvert.cs:103`
  - `BusinessLockSystem.cs:82`
  - `CoinManagement.cs:70`
  - `LocalTimeCorrection.cs:37`
  - `MallSystem.cs:156` (hardcoded mac `VDartsH2_929`)
  - `Test_NewWork.cs:49`
  - `cardPlayerSystem.cs:1920`
  - `fpfull/Game.cs:422` `API_MachineState_Parse`
- **Response fields:**
  - `mac`: the registration check needs it longer than 3 chars.
  - `name`
  - `company_name`: `ToString()` with quotes becomes `Game.Conpany_name`.
  - `is_locked`: must be `"false"`; `AsBool` in BusinessLockSystem.
  - `lock_after` and `server_time`: passed to `Convert.ToDateTime`, so use format `yyyy-MM-dd HH:mm:ss`. A missing value throws in some callers. When `server_time > lock_after`, the fee is expired and card login is blocked.
  - Error: `{"error":"invalid mac"}`, compared as the quoted string.
- **Sample:**
```json
{"mac":"00-30-18-04-10-DB","name":"Machine1","company_name":"Shop","is_locked":"false","lock_after":"2099-12-31 00:00:00","server_time":"2026-11-17 12:00:00"}
```
`server_time` is also used for clock correction and by `IsBackgroundCanUse`, so return the real server time.

## 5. GET /echo.php (APICore latency test)
- **Schedule:** every 10-600 s on both bases.
- **Parsing (https probe only):** the client finds `"cip"`, takes the text up to the next `,`, and strips spaces, `:` and `"`. A result of 7 or more chars becomes `MyIP`. A comma must follow, so `cip` cannot be the last key.
- **Sample:** `{"cip":"203.0.113.5","time":"2026-11-17 12:00:00"}`.
- **Speed:** a fast reply is the only other requirement.

## 6. GET /gamePriceGet.php (CoinAPI.cs:24, then PriceForm.upDataPriceForm)
- **Request:** `?mac=<mac>`.
- **Response:** a **top-level array**:
```json
[{"game":"201","cost":{"pay.gft":"2","pay.vch":"2","pay.coi":"2"}},{"game":"998","cost":{"pay.gft":"2","pay.vch":"2","pay.coi":"60"}}]
```
- **Parsing:**
  - Values go through `int.Parse`.
  - Index 0 is gft, 1 is vch (card), 2 is coin.
  - Defaults are 2/2/2; `100000` means disabled.
  - Game `998` coin value is the max offline timeout; below 10 disables it.
  - Exceptions are swallowed and the previous data kept, so `[]` is acceptable.

## 7. POST /gameRoundGet.php (GameRound.cs:37)
- **Request:** `machine_mac=<mac>`.
- **Response:** `{"round":{"201":"10","301":"20"}}`. Keys and values go through `int.Parse`; a bad value makes it retry every frame.
- **Minimal:** `{}`. Refreshed every 2 h.

## 8. POST /machineCheckCoin.php (Money.cs:51; timeout 25 s)
- **Request:** `mac=<mac>&insert=<ulong>&consume=<ulong>`.
- **Response:** `{"insert":"N","consume":"N"}`. The client keeps the max of local and server; echo the received values back.
- **Failure:** an empty response marks the machine offline.

## 9. POST /gameVdartsRecord.php (DataPush.cs:278; retry 9, timeout 30 s)
- **Request:** `game_type=<id>&group_by=<..>&records=<RAW JSON array>&league_id=&league_order=&leagueor_leg=&winner=`. Note the typo `leagueor_leg`. There is **no token**.
- **Record shape:** `{member_id, machine_mac, effect{Bull,DBull,HatTrick,LowTon,HighTon,ThreeInABed,ThreeInTheBlack,Ton80,WhiteHorse,Mark5..Mark9}, rating, score, record[[...]], handicap_score, version}`.
- **Response:** ignored; return `{"result":"ok"}`. This is where ratings/stats would be updated.

## 10. POST /vpointAdd.php (gameSystem.cs:1601, OISystem.cs:2822, cricketSystem.cs:2771)
- **Request:** `token=<128-char HEX java token>&action=game&mac=<mac>&gameType=online|basic|medley`.
- **Response:** ignored. To identify the player, hex-decode the token to the 64-char game_token.

## 11. GET /vpointGet.php (MallSystem.cs:1321, onlineMenuSystem.cs:110)
- **Request:** `?token=<game_token>`.
- **Response:** `{"vpoint":"123"}`. The value goes through `int.Parse`, so it must be an integer.

## 12. GET /consume.php (CoinController.cs:152)
- **Request:** `?data=[{"type":"pay.mic","mac":"..","member_id":"..","game":0,"coin":N}]`. This is raw JSON in the query string (JsonUtility of `PlayerPaysCoin`), not encoded.
- **Response:** ignored.

## 13. POST /consumeCardToCoin.php (cardPlayerSystem.cs:773)
- **Request:** `card_no&machine_mac&value`.
- **Success:** `{"success":"1"}`. The response must be longer than 8 chars.
- **Errors:** `{"error":"shop"}` or `{"error":"pay.vch"}` (insufficient).

## 14. GET /extPayUrl.php (cardPlayerSystem.cs:1755)
- **Request:** `?mac=`.
- **Response:** `{"url_qrcode":"..","url_confirm":".."}`. External QR pay activates only when both are longer than 10 chars. Return `{}` to disable it.
- **Confirm URL:** if enabled, the confirm URL must return `{"result":"ok","mac":..,"pay":..}`.

## 15. POST /checkKeyCard.php (NewMenu2D_Setting.cs:566)
- **Request:** `card_no&machine_mac`. The client loops until the response is non-empty.
- **Response:**
  - A non-empty `error` aborts.
  - Otherwise `{"data":{"is_paid":"1","is_key_card":"1","qr_code":"..","start_date":"..","end_date":".."}}`.

## 16. POST /gamePrice.php (NewMenu2D_Setting_Pay.cs:529)
- **Request:** `card_no&machine_mac&list=[{"game":"201","price":"2"},...]&consume_type=pay.coi`, raw JSON.
- **Response:** success means `error` is missing or empty, e.g. `{"result":"ok"}`. Persist the prices so gamePriceGet returns them.

## 17. POST /gameRound.php (NewMenu2D_Setting_Round.cs:512)
- **Request:** `card_no&machine_mac&list=[{"game":"201","round":"10"}]`.
- **Response:** `{"result":"ok"}`. Persist the rounds for gameRoundGet.

## 18. GET /leagueMatchList.php
- **Request:** `?mac=`.
- **Response:** **must be a top-level array**; it is cast to `JSONArray`, so an object throws.
- **Items:** `{"id":..,"seaon":..(sic),"name":..,"team_name1":..,"team_name2":..}`.
- **Minimal:** `[]`.

## 19. GET/POST /leagueMatch.php (LeagueSystem.cs:467, LeagueDataCtrl.cs:222/257/431)
- **GET request:** `?mac&id`.
- **Detail fields:** `season, name, id, win` (non-zero means ended), `score1, score2, nowGameCount, nowGameLeg`.
- **Teams:** `team1`/`team2` each `{name, logo, shop, member:[{name, account, rating, cardnumber}]}`. **`rating` is a JSON-encoded STRING**, e.g. `"{\"average\":\"A5\"}"`; it is re-parsed, so an empty or plain value crashes.
- **`match_detail[]`:** `{order, player_count, rating_limit_*, is_let_rating_score, bust, surrender, score1, score2, win, team1[], team2[], legs:[{order, game, setting{g01_in,g01_out}, win, rating_1[], rating_2[], game_round}]}`.
- **POST** `mac&id&data` (raw JSON): success means an empty `error`.
- Only reachable if leagueMatchList is non-empty.

## 20. GET /leagueTeamRank.php (LeagueDataCtrl.cs:497)
- **Response:** `{"list":[{"season":"1","rankings":[{"order":1,"team":"T","score":0,"nation":"TW"}]}]}`.
- **Minimal:** `{"list":[]}`.

## 21. GET /fxPersonal.php
Request variants and responses:
- `?token=<game_token>` returns `{"personal":[{fx,type,expiry_date,target}],"used":[{fx,type,target,addtion}]}`.
  - `type` is one of `fx_.vid` / `fx_.wav` / `fx_.hit` / `fx_.plb` / ...
  - `target` is `gam.01g` / `gam.crk` / `gam.cup` / `""`.
  - Minimal: `{"personal":[],"used":[]}`.
- `?token&member_id` (opponent fx): same shape.
- `?type=<fx type>&language=..&price_json=1` returns a top-level array of `{i18n_index,name,url,type,game_ver,price_array:[{time,price}x3],addtion}`. Minimal: `[]`.

Callers:
- `MallSystem.cs:203, 413, 543, 855`
- `League_MainScreen.cs:729`
- `onlineMenuSystem.cs:1024` (loops until non-empty)

The `:8043` variant is used after login (section 1).

## 22. POST /fxGetUsed.php (MallSystem.cs:499/524, PlayerInfo.cs:161)
- **Request:** `token=`, or `card_no&machine_mac`.
- **Response:** `{"txt":{"fx_.txt.bul":"..",...},"bak":{"gam.01g":{"url":".."}},"dbg":{},"mbg":{}}`.
- **Minimal:** `{"txt":{},"bak":{},"dbg":{},"mbg":{}}`.

## 23. POST /fxGetAllBak2.php (mall backgrounds)
- **Request:** `is_show_price=t&token&language&price_json=1`.
- **Response:** `{"list":{"fx_.bak.001":{"name":..,"url":..,"version":..,"expiry_date":..,"price_array":[..]}}}`.
- **Dates:** `IsBackgroundCanUse` calls `DateTime.Parse(nowDate)`, using machineStatus `server_time`.
- **Minimal:** `{"list":{}}`.

## 24. GET /fxGetAllBak.php (EfxDownloader.cs:59)
- **Response:** an object whose values are file basenames. For each value the client GETs `/download/fx/bak/<name>.jpg`, which must reply with `Content-Type: image/*`.
- **Caveat:** the URI is built with `string.Concat(node, ".jpg")`. If that calls `ToString()`, the name gets quotes (`"name".jpg`), which looks like a client bug.
- **Recommended:** `{  }` (longer than 3 chars, no keys, nothing downloaded). A bare `{}` makes the coroutine wait forever, which is harmless.

## 25. POST /lottery.php (GameOverSystem.cs:483; only if DevelopMenu.formalLotty)
- **Request:** `mac=<mac>&token=["<game_token>","<game_token>"]`, raw. The online-mode builder omits commas between items.
- **Response:** `{"image_path":"","list":[{"token":..,"item_name":"null","image":..,"type":..,"prize_level_id":..}]}`, or `{"error":".."}`. An `item_name` of `"null"` means no prize.

## 26. /supervMachineSchedule.php (SuperVManager.cs)
- **GET** `?mac=` (`:99`, only polled in SuperV mode): `{}`. A `dealer` other than `superv` disables SuperV.
- **POST** `mac&jsonData=<EscapeDataString JSON>` (`:581`): `{"info":"success"}`.

## 27. /vpointBet.php (VDart PK betting: onlineMenuSystem.cs:265/1195, GambleRankings1.cs:155)
- **GET `?type=bet`:** `{"data":{"is_locked":"0","fee":"0","amount":["0","10","50"]}}`.
- **GET `?type=fee`:** `{"data":{"fee":"0"}}`.
- **GET `?type=ranking&page=N`:** `{"data":[{"nickname":..,"vpoint":..,"nation":..}]}`.
- **POST** (raw JSON body: invite/deposit/settlement): `{"data":{"match_id":"1"}}`, or `{"error":".."}`.

## 28. /league3MatchList.php (V-Crown: VCrownTable.cs:163/214/246/761, VCrownGamesData.cs:110/202, onlineMenuSystem.cs:1260)
- **GET** `?type=match&token=`: `{"error":"","data":[]}`. An empty data array hides the feature.
- **GET** `?type=ladder|group_match&match_id&token`: `{"data":{"game":..,...}}`. Sample strings are embedded in `VCrownGamesData.cs`.
- **POST** `token&match_id&round_id&group_id&player1&player2&score1&score2&winner`: success means an empty `error`. Values may arrive with literal quotes, e.g. `match_id="190"`, because of `ToString()`.

## 29. /league4Match.php (IE_API: V-Crown double)
- **GET** `?requesttype=get_match_list&page=1&type=4&token=`: `{"code":"0","msg":"ok","data":{"list":[]}}`.
- **GET** `requesttype=get_match_group` / `group_match`: `data{match_type,round_id,group_id,winner,score1,score2,nickname1,nickname2,type,player1,player2,game}`.
- **POST** `requesttype=edit_match_group` / `add_next_round`: `{"code":"0","msg":"ok"}`.

## 30. /league4Vteam.php (VTeam)
- **GET** `?requesttype=apply_fee_list` (`VTeamMenu.cs:90`): **a raw integer body such as `0`, not JSON** (`int.Parse`).
- **POST** `requesttype=add_match` (`cardPlayerSystem.cs:2374`): `{"message":"ok","data":"123"}`. `data` is the int match id.
- **POST** `apply_match` (`cardPlayerSystem.cs:2407`) and `update_match` (`GameOverType.cs:144`): any non-empty body.

## 31. /machineCheckOnline.php (IE_API: NetworkLinkTimeCollector.cs:45/72)
- **GET** `?mac=`: `{"status":"1","data":{"minute":"0"}}`. `minute` 0 disables the collector for a day.
- **POST** `mac&minute`: `{"status":"1"}`.
- **Errors:** `{"status":"0","error":"invalid_minute|less_than_0.8|machine_not_exists|add_log_error"}`.

## 32. /cancelAccount.php (IE_API: NewMenu2D_Member.cs:189/191)
- **GET** `?token=`: `{"status":"1","data":{"timestamp":"1763380800"}}`.
- **POST** `token&timestamp&sign`:
  - `sign = MD5(member_id + timestamp + token + uuid)`, uppercase hex.
  - Success: `{"status":"1"}`.
  - Errors: `invalid_token, params_error, already_del, del_error, sign_error, timeout`.

## 33. /memberToken.php
- `API.UploadSetGameToken` has no callers and a missing `&` bug.
- `PHP_API.GetTokenData`: GET `http://api.vdarts.net/MemberAPI/memberToken.php?token=` returns `{"token":".."}`.

## 34. PHP_API.cs (fpfull / full)
- **`GetRank`:** POST `http://api.vdarts.net/MemberAPI/gameRecordGetRanking2.php` with `token&game`. Response `{"self":{"rank":"1"}}`. Caller `NewMenu2D.cs:679-698` (settings key card via Java login).
- **Java endpoints (not PHP):**
  - `GetAccount`: `:8080/MemberPlatformWS/card`
  - `test_MemberLogin`: `/member/login`
  - `GetPlayerInfo`: `/member?mac&card` with header `WWW-Authenticate: <token>`; needs `member_id` and a response longer than 10 chars (`fpfull/Game.cs:163/218`)

## 35. LAN.cs: not HTTP
- UDP broadcast `vdarts` to port 8192 across local /24 networks, plus TCP `mac ` probes.
- Used for the double-camera pairing (`DoubleCameraAdapter.cs:180-255`). No server needed.

## 36. Unused API methods
`DownLoadTeamTexture`, `GetAllFxPersonal`, `DeleteBackgroundFx` (fxUsedDel.php), `UploadSetGameToken`.

## 37. Hardcoded URLs outside APICore (`fpfull/ServerIpControl`)
These bypass the base selection:
- `https://api.vdarts.net:8043/MemberAPI/` for fxPersonal, fxUsedEdit, fxTextEdit, lottery, signup, messageGet, advertisementGet
- `http://t1.vdarts.net/versionVpoint.php`
- Java `:8080`

A replacement server must also listen on **8043 (https)**, or these hosts must be redirected.

## Minimal server checklist
1. Ports: https 443, http 8000, https 8043 for api.vdarts.net (self-signed is OK).
2. `/echo.php` and `/machineStatus.php` with valid dates and `is_locked:"false"`.
3. `/gamePriceGet.php` returning `[]`, and `/gameRoundGet.php` returning `{}`.
4. `/login.php` (card) with a 64-char `game_token`, at least a 10-char `token`, `card_type:"crd.mem"` and a large balance.
5. `/fxPersonal.php` (also on :8043) returning `{"personal":[],"used":[]}`.
6. `/fxGetAllBak.php` returning `{  }`.
7. Everything else can return `{}` or `[]`, except:
   - `league4Vteam apply_fee_list` returns `0`
   - `leagueMatchList` returns `[]`
   - `vpointGet` returns `{"vpoint":"0"}`
