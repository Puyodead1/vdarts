# VDarts HTTP catalog: business / lock / ads / announcements / mall / feedback / misc

Paths: `full/` = Assembly-CSharp decompile, `fpfull/` = firstpass. All line numbers refer to those files.
Out of scope (other agents): `API.cs`/`PHP_API.cs`/`APICore` calls (`/MemberAPI/*.php` via APIDat, including
`gameRecordGetRanking2.php` = `PHP_API.GAME_RECORD_GET_RANKING`, PHP_API.cs:10), and PlayerInfoPool member login /
`/member/login`, `/member`, `/is_gamekey`, `/record`.

## 0. General notes that apply to every endpoint below

* **JSON library** is `I2.Loc.SimpleJSON` (fpfull/I2.Loc.SimpleJSON). Quirks that matter:
  * `node["k"]` on a missing key returns a `JSONLazyCreator`; implicit `(string)` of that is **null**,
    `.Value` is `""`, `.Count` is 0, `.AsInt/.AsBool/.AsFloat` are 0/false.
  * `node["k"].ToString()` on a string leaf returns the value **with quotes** (`"\"abc\""`). Code that compares
    `ToString()` against `"\"invalid mac\""` etc. expects JSON *strings*, not numbers.
  * `JSONNode == object` is **reference equality** (JSONNode.cs:213). So code like `json["error"] == 0` or
    `json["error"] == (object)"1"` is always **false** no matter what the server sends (see BusinessPage).
    `(string)json["status"] == "1"` is a real string compare and works.
  * `JSON.Parse("")` returns null; `JSON.Parse(null)` throws NRE.
  * All values are best sent as JSON strings (`"1"`, not `1`) – numbers are parsed as strings anyway, but quoting is
    the safe choice for `ToString()` comparisons.
* All requests use legacy `UnityEngine.WWW` (GET when no form, POST `application/x-www-form-urlencoded` when a
  `WWWForm` is given; **multipart/form-data** when `AddBinaryData` is used). Query strings are built by plain string
  concatenation – **no URL encoding** of values.
* `WWW.error` is non-null on transport failure **and** on HTTP status >= 400. Most callers treat any 200 as success.
* HTTPS hosts (`https://api.vdarts.net:8043`, `https://t1.vdarts.net`) go through Unity's default certificate
  validation (no custom handler on these WWW calls) – the replacement server needs a cert the machine trusts, or keep
  plain HTTP via DNS + a patched constant. (`VdartsHttpsCertHandler`, used only by dead code in NewMenu2D/NewMenu2D_Base,
  accepts any cert whose Issuer contains "Let's Encrypt" or whose Issuer/Subject contains "letswinvdarts" at index>0.)

---

## 1. LOCKING / BLOCKING behaviour (read first)

### 1a. Offline-time lock (the only live lock) – `PublicStuShow` + `LockGame`
* `AddOfflineTimeCounter` (full/AddOfflineTimeCounter.cs:16) – because `DevelopMenu.LOCKGAME_48HOURS = true`, it adds 10 s
  to `PlayerPrefs["offOnlineTime"]` every 10 s, **unconditionally**.
* The counter is reset only by `PublicStuShow.sOnlineShow(1)` (fpfull/PublicStuShow.cs:117-123) which is called when
  `API.UploadConsumeCoin` (`/machineCheckCoin.php`, other agent) returns non-empty text (full/Money.cs:50-61), on
  successful member card login (cardPlayerSystem.cs:2166, League_MainScreen.cs:702), or by LockGame.
* `sOnlineShow(2)` (Money.cs:56 when machineCheckCoin returns ""/fails; Money.cs:112; cardPlayerSystem.cs:1228;
  onlineMenuSystem.cs:337) calls `CheckOfflineTimeisExceeds()`: locks if `PlayerPrefs["IsLock"]==1` **or**
  `m_maxOfflineTimeout > 0 && offOnlineTime >= m_maxOfflineTimeout*60`.
  `m_maxOfflineTimeout` = price-form entry **998 (`GameType.OfflineLimit`) column [2]** (PriceForm.cs:35/72);
  values < 10 disable the lock. The price form is pushed by the API agent's endpoints – **send 0 for item 998**.
* When locked: `Game.isLocked = true` (NewMenu2D.cs:467 ignores all menu keys), `PlayerPrefs["IsLock"]=1` (persists across
  reboots!), and `PopupManager/LockGameMessage` is activated → `LockGame.OnEnable`.

### 1b. `LockGame` unlock poll – `POST {BIG_BOARD_SERVER_IP}/coin`
* URL: `http://api.vdarts.net:8080/VDartsAPIs/coin`  (full/LockGame.cs:41-63)
* Method: POST form: `insert=0`, `consume=0`, `mac=<Game.GetMachine()>`.
* Sets `LockGame.LOCKKEYBOARD = true` (keyboard.cs:9, menuSystem.cs:92 ignore input) while enabled.
* Response body is **ignored**; only `string.IsNullOrEmpty(www.error)` matters. On success → `sOnlineShow(1)` (clears
  IsLock and offOnlineTime), LOCKKEYBOARD=false, popup hidden. On failure → retries **every 300 s**.
* Same component is on `LockGameMessage_ConnectFee` / `LockGameMessage_InvalidMac` popups (any of them is cleared by a
  200 from /coin).
* **Minimal response:** HTTP 200 with any body, e.g. `{"result":"0"}`. This endpoint must exist or a locked machine
  stays locked (5-minute retry).

### 1c. BusinessLockSystem – dead code
* full/BusinessLockSystem.cs: `Start`, `CheckLock`, `Update` are empty; `PingSever` (GET `https://www.vdarts.net`, :43,
  plus ICMP pings to 223.5.5.5 / 114.114.114.114) and `IE_WaitChahBoxConnect`/`CheckCashBoxIsLock` (which would show
  `LockGameMessage_InvalidMac` if `machineStatus.php` returns `{"error":"invalid mac"}` or `LockGameMessage_ConnectFee`
  if `"is_locked":"true"`) are **never invoked**. `LockGame.IECheckToRemoveLockGame_Business` (GET
  `https://www.vdarts.net`, LockGame.cs:66-104) is also never started. Nothing to implement; for safety
  `https://www.vdarts.net` returning 200 is harmless.
* Note: `machineStatus.php` (API agent) is still parsed elsewhere with the same fields (`error` == `"invalid mac"`,
  `is_locked`, `server_time`, `lock_after`) – e.g. MallSystem.cs:150-168 reads `server_time`.

### 1d. BusinessPage (business account login overlay) – dormant
* Only shown if `GlobalData.IsBusiness` is true (BusinessPage.cs:47) and `PlayerPrefs["BusinessLogin"] != "Logined"`.
  `GlobalData.IsBusiness` is initialised `false` (fpfull/GlobalData.cs:101) and **never assigned** anywhere
  (GameParams.IsBusiness is a different property). So the page never appears. Endpoints documented in §2 for completeness.

### 1e. Things that do NOT block boot/game start
Ads, announcements, scrolling text, update progress, version check, mall, feedback, league team logos, FX lookups all
fail soft (log + skip). The only per-scene wait is online-match `WaitToLoadLevel` (onlineMenuSystem.cs:1364) on
`isGettingOnlinePlayerData`, but the coroutine that sets it (CustomText_IP) is never called.

---

## 2. Business account (dormant, see 1d)

### 2.1 `POST http://api.vdarts.net:8080/VDartsAPIs/businessLogin`  (ServerIpControl.BUSINESSLOGIN_IP)
* Caller: full/BusinessPage.cs:136-169 (`IE_Login`).
* Form (urlencoded): `acc`, `pass`, `box_mac` (= Game.GetMachine()).
* Parsing: `JSON.Parse(text)["error"]` compared with `== 0`, `== (object)"1"`, `== (object)"2"` → **reference
  comparisons, always false** (§0). Therefore even a perfect response does nothing: page stays open, no message.
  HTTP error → popup `Login_LoginFail`.
* Intended response: `{"error":"0"}` success, `"1"` bad login, `"2"` box already bound.
* Sample: `{"error":"0"}`

### 2.2 `POST http://api.vdarts.net:8080/VDartsAPIs/businessRegistered`  (BUSSINESSREGISTERED_IP)
* Caller: full/BusinessPage.cs:198-235.
* Form: `acc`, `pass`, `box_mac`, `email`.
* Same broken comparisons; intended codes `"0"` ok, `"1"` account exists, `"2"` email exists, `"3"` box exists.
* Sample: `{"error":"0"}`

### 2.3 `BUSINESSBOX_IP` (`.../VDartsAPIs/businessBox`) – defined but **never referenced**.

---

## 3. Advertising (idle attract loop)

### 3.1 `GET http://api.vdarts.net:8000/MemberAPI/advertisementGet.php?mac=<Game.Mac>`  (ADVERTISING_IP)
* Caller: full/JsonAdvertising.cs:30-72, driven by full/AdvertisingSystem.cs:36-95.
  `DevelopMenu.IDLEADVERTISING = true` so it runs on every machine once AdvertisingSystem's LateUpdate fires:
  first waits until `Game.GetMachine() != ""`, then requests; **retries every 60 s** (`ADVERTISINGRESENDTIME`) until
  a parse succeeds; then refreshes every 120 min.
* Params: `mac` raw, not encoded.
* Response: **top-level JSON array**. For each element only `["url"]` is used:
  * elements whose `url.ToString()` contains `.mp4` are dropped;
  * remaining `url` strings become `Advertising.advertisingName[]`.
  * Each url **must contain `/ads/`** – the part after it becomes the local path `persistentDataPath/Ads/<rest>`
    (JsonAdvertising.cs:84-88, AdvertisingSystem.cs:178-189, Advertising.cs:87-89). A url without `/ads/` →
    IndexOutOfRange, killing the download coroutine (no recovery until restart).
  * Picture ads should live under `/ads/pic/...`, videos under `/ads/video/...` (`.ogv` only – Theora MovieTexture);
    files in local `Ads/pic` / `Ads/video` not listed are deleted (AdvertisingSystem.cs:98-166).
  * Video detection: url contains `.ogv` or `.mp4`.
* File download: plain `GET <url>` via `DownloadSystem.ToPersistentDataPath` (DownloadSystem.cs:27-63); only if the
  file does not already exist; on HTTP error logs and retries every 60 s until all files exist.
* Missing `url` key → null string → `ArgumentNullException` in Regex.Split; a JSON object instead of array (e.g.
  `{"error":"x"}`) has Count 1 and crashes the same way. HTTP error → just retries in 60 s.
* Playback (Advertising.cs:68-131): when the menu is idle, an `Advertising` prefab is put on top of PopupManager; with
  an empty list it waits forever (fully transparent, 60 s polls) – harmless. With ≥1 ad it cycles ads, and after each
  full cycle shows `Rankings.ShowAll()` and `DailyRankings.ShowAll(yyyy-MM-dd)` (ranking endpoints = other agents).
  When idle ends, `DestoryAdvertisinObj` calls `NewMenu2D_Announcement.StartOpenAnnouncementDescription()` → opens the
  announcement panel and refetches messageGet (see §4.1).
* **Minimal stub:** `[]` (ads disabled, no ranking slideshow). To get the attract loop with ranking pages:
  ```json
  [ { "url": "http://api.vdarts.net:8000/ads/pic/vdarts_logo.png" } ]
  ```
  and serve that PNG/JPG with HTTP 200.

### 3.2 AdsSystem / AdsDownloader – dead
* full/AdsSystem.cs:92-99 `AdsPicDownload` (GET `http://3.im.guokr.com<picname>`) is never started; `refreshads` is a
  no-op. AdsDownloader.cs is empty. Nothing to serve.

---

## 4. Announcements

### 4.1 `GET http://api.vdarts.net:8000/MemberAPI/messageGet.php?lang=<l>&source=11[&token=<t>]`  (Announcement_IP)
* Callers:
  * full/NewMenu2D_Announcement.cs:186-236 (`IE_GetAnnouncement`) – started by `OnOpenAnnouncement` (menu button),
    `StartOpenAnnouncementDescription` (after every member login, NewMenu2D_Member.cs:355, and every time an idle ad
    stops, Advertising.cs:226), and on item click.
  * full/ScrollingText.cs:27-68 – once at `Start()`, then 300 s after each scroll completes.
* Params: `lang` ∈ zh-tw, zh-cn, en-us, fr-fr, es-es, ja-jp, ko-kr; `source=11`; `token` = ASCII token of logged-in
  player 0 (hex-decoded), omitted when nobody is logged in. Not URL-encoded.
* Response parsed:
  * `error` – if non-empty string → abort (no UI update).
  * `data` – array; per item: `title`, `content`, `update_time`, `message_id`, `is_read` ("0" = unread → red icon),
    `media_data` (array of `{type:"image"|"video", thumbnail, url}`).
  * `media_path` – string prefix prepended to `thumbnail` / `url` for media downloads.
  * ScrollingText uses `data[0].title` only.
* Failure behaviour: HTTP error/empty body → silently abort (panel stays empty). **Empty `data` array is NOT clean**:
  `ShowSelectItemFrame()` indexes `itemList[0]` → ArgumentOutOfRange (coroutine dies after setting the icon); keys
  Right/OK on the open panel throw each press (Left closes it); `StartOpenDescription` with no data passes nulls;
  ScrollingText does `null.ToCharArray()` → NRE and never re-schedules (marquee simply never shows). None of these
  block gameplay, but the cleanest stub is **one item**:
  ```json
  {
    "media_path": "",
    "data": [
      { "message_id": "1", "title": "Welcome", "content": "Server restored.",
        "update_time": "2026-11-17 00:00:00", "is_read": "1", "media_data": [] }
    ]
  }
  ```
  Note: the ScrollingText marquee shows `data[0].title` every ~5 min and the announcement panel auto-opens on login and
  after every idle-ad session (original behaviour).
* Media (NewMenu2D_AnnouncementDescription.cs:126-176): `GET media_path+thumbnail` (texture; `_www.texture.width`
  used unconditionally, so must be an image), for `video` also `GET media_path+url`, written to
  `StreamingAssets/AnnouncementVideo.ogv|.mp4` and played as MovieTexture from `.ogv`. Keep `media_data: []`.

### 4.2 `GET http://api.vdarts.net:8000/MemberAPI/messageViewLog.php?message_id=<id>&source=11&device=<mac>`  (AnnouncementRead_IP)
* Caller: full/NewMenu2D_AnnouncementItem.cs:74-101 (`IE_Read`), once per logged-in token (token is never actually
  appended – `text` is always "").
* Response: `error` (non-empty → log), `status` == "1" → log. Purely informational.
* Sample: `{"status":"1"}`

---

## 5. Feedback

### 5.1 `POST http://api.vdarts.net:8080/VDartsAPIs/suggestion`  (SEND_FEEDBACK_IP) – multipart/form-data
* Callers: full/NewMenu2D_Setting_FeedBack.cs:70-101 (requires a logged-in player), full/NewMenu2D_Feedback_1.cs:45-78,
  full/PageFeedBack.cs:75-101.
* Fields: `id` (member_id of player 0), `type` ∈ `sug.bug|sug.crh|sug.blt|sug.opi|sug.oth`, `opinion` (text; Setting
  variant appends `【machine】: <deviceName>`), file `photo` (filename `test.jpg`; real PNG/JPG bytes `image/png`, or a
  single 0x00 byte `application/octet-stream` when no picture).
* Response body ignored. HTTP 200 → popup `Assistance_FeedbackComplete` (Setting/PageFeedBack); error → log / "Send
  Feedback Failed." (Feedback_1).
* Sample: `{"result":"0"}` (any 200).

---

## 6. Mall / FX (personal effects, backgrounds, custom text)

Mall listing/vpoint calls go through API.cs (`API.GetFxPersonal`, `API.GetAllFx`, `API.GetBackgroundFxUsed`,
`API.VPointGet`, `API.GetMachineStatus`) – other agent. Direct WWW calls:

### 6.1 `GET https://api.vdarts.net:8043/MemberAPI/fxPersonal.php?token=<ascii token>`  (FxPersonal_IP)
* Callers: full/cardPlayerSystem.cs:2201-2247 (card login), full/NewMenu2D_Member.cs:374-400 (member login).
  (League_MainScreen.cs:719 uses `API.GetFxPersonal` instead because `formalFX = true`.)
* Response stored in `PlayerInfoPool.customEffectJson[n]` and parsed by cardPlayerSystem, GoWhichGame.cs:191-218,
  PlayerLoginSystem.cs:392, onlineMenuSystem.cs:985 and the Mall:
  * `used[]`: `{ "fx": "<fx id>", "type": "fx_.vid.*|fx_.wav.*|fx_.hit.*|fx_.plb.*", "target": "gam.01g|gam.crk|gam.cup|\"\"", "addtion": "MovieMerge|..." }`
    – `type` must be a non-null string for every element (`text.StartsWith` NRE otherwise).
  * `personal[]`: `{fx, type, expiry_date, target}` (Mall expiry lookup, MallSystem.cs:876-881).
  * `list{ key: {expiry_date, version, url, ...} }` (Mall, MallSystem.cs:890-894, 969-985).
* On HTTP error `customEffectJson` stays `""` → `JSON.Parse("")` = null → all consumers early-return. Not blocking.
* **Minimal:** `{"personal":[],"used":[],"list":{}}`
* Sample with one effect (the client's own built-in test data, LeagueMainScreen.cs:752):
  ```json
  {"personal":[{"fx":"fx_.vid.001","type":"fx_.vid.bul","expiry_date":"2099-12-31","target":""}],
   "used":[{"fx":"fx_.vid.005","type":"fx_.vid.bul","target":"","addtion":"MovieMerge"}],
   "list":{}}
  ```

### 6.2 `POST https://api.vdarts.net:8043/MemberAPI/fxPersonal.php` – purchase effect/sound  (FxPersonal_IP)
* Caller: full/MallSystem.cs:1265-1313 (`IE_BuyEffectAndSoundItem`).
* Form: `token`, `i18n_index` (e.g. `fx_.vid.001`), `time_val` (days), `action=purchase`, `price_json=1`,
  `game_type=vdarts`.
* Parse: `info`. **Empty/missing `info` = success** (then refreshes vpoint and FX JSON via API). Known `info` errors:
  `time_val error`, `member vpoint insufficient` (offers to open StoredValue page), `day error`. Empty body →
  `Popup_NetCheckFail`.
* Sample success: `{"status":"1"}`

### 6.3 `POST https://api.vdarts.net:8043/MemberAPI/fxPersonal.php` – use effect/sound
* Caller: full/MallSystem.cs:1429-1491 (`IE_UseEffectAndSoundItem`).
* Form: `token`, `i18n_index`, `gam_val` (gam.01g/gam.crk/gam.cup), optional `used_type`, `action=used`.
* Parse: `info` empty = success. Errors: `can only be used gam_crk`, `game target error`, `no purchase`,
  `gam_val error`, `used_type null`, `wav type error`, `passed its expiry_date`.
* Sample: `{"status":"1"}`

### 6.4 `POST https://api.vdarts.net:8043/MemberAPI/fxUsedEdit2.php` – buy background  (FxBuyBackground_IP)
* Caller: full/MallSystem.cs:1202-1263.
* Form: `token`, `img_index` (fx_.bak/.dbg/.mbg key), `time_val`, `price_json=1`, `game_type=vdarts`.
* Parse: `status` == "1" → success, shows `expiry_date`; else `info` ∈ `item null`, `member vpoint insufficient`,
  `PURCHASED_TIME_PROMPT_1`, `PURCHASED_TIME_PROMPT_2`, `data error`, `time_val error`.
* Sample: `{"status":"1","info":"","expiry_date":"2099-12-31"}`

### 6.5 `POST https://api.vdarts.net:8043/MemberAPI/fxUsedEdit.php` – use background  (FxUseBackground_IP)
* Caller: full/MallSystem.cs:1385-1427.
* Form: `token`, `img_index`, `gam_val`, `crk_01g_cup=t`.
* Parse: `status`=="1" success; `info` ∈ `img time expired`, `img is null`. Loading spinner is always cleared when
  body non-empty; empty body → NetCheckFail.
* Sample: `{"status":"1"}`

### 6.6 `POST https://api.vdarts.net:8043/MemberAPI/fxUsedDel.php` – remove background  (FxDelBackground_IP)
* Caller: full/MallSystem.cs:1342-1371.
* Form: `is_del=1`, `gam_val`, `token`.
* Parse: `status`=="1" → refresh. Sample: `{"status":"1"}`

### 6.7 `POST https://api.vdarts.net:8043/MemberAPI/fxTextEdit.php` / `fxTextDel.php` – custom text  (FxSetCustomText_IP / FxDeleteCustomText_IP)
* Caller: full/MallSystem.cs:1500-1556. Set when input non-empty, delete otherwise.
* Form: `token`, `type` ∈ `fx_.txt.bul|dbl|htk|htn|ltn|bed|bak|ton|whs`, `text` (set only).
* Response body not parsed; **any non-empty body** = `Mall_SetSuccess`. Sample: `{"status":"1"}`

### 6.8 Preview images – `GET https://api.vdarts.net:8043/MemberAPI/download/fx/video/<url>.jpg` and `.../fx/bak/<url>.png`
* (FxEffectPreview_IP / FxBackgroundPreview_IP) Callers: full/Mall_FourItem.cs:231-247 (vid), 267-283 (bak/dbg).
  `<url>` is the item's `url` field from the FX list JSON. Cached in `persistentDataPath/Mall/`. Error → logged, icon
  stays default. Serve images or 404.

### 6.9 Browser links (Application.OpenURL – not HTTP calls by the game)
* `https://api.vdarts.net/vpointRechargePackageApp.html?token=..&appid=vdartsgame&lang=..` (StoredValue_IP,
  MallSystem.cs:1575), `https://www.vdarts.net/vshop/` (:1583), `REGISTER_URL`, `/version/<v>` (VersionCheck).

### 6.10 `POST https://t1.vdarts.net/MemberAPI/versionVpoint.php`  (VersionVPoint_IP)
* Caller: full/NewMenu2D_Member.cs:359-372 after each member login (fire-and-forget).
* Form: `token` = **hard-coded constant** `4b05f4eb...fce4` (not the player's), `platform=vdarts_game`,
  `version=02.10.04`.
* Response: only `JSON.Parse(text)["error"]` evaluated and discarded. Sample: `{"status":"1"}`.

### 6.11 `CustomText_IP` – `GET http://api.vdarts.net:8080/VDartsAPIs/fx?memberid=<id>` – dead
* full/onlineMenuSystem.cs:1351-1361; the wrapper `GetOnlinePlayerCustomText` is empty, never called. Would parse
  `text.{bull,double_bull,low_ton,high_ton,white_horse,hat_trick,ton80,three_in_the_black,three_in_a_bed}`.

### 6.12 `Lottery_IP` – unused constant; lottery goes through `API.GetLottery` (GameOverSystem.cs:479-509, other agent).
* Bonus-item images: `GET https://api.vdarts.net:8043/<image_path><image>` (GameOverSystem.cs:542-553), quotes
  stripped from both fields of the lottery JSON. Error → logged, `null` texture shown. The non-formal branch
  (`POST t1.vdarts.tw/MemberAPI/lottery.php`, :512-537, hard-coded mac/token) is dead (`formalLotty = true`).

---

## 7. Register (new member)

### 7.1 `POST https://api.vdarts.net:8043/MemberAPI/signup.php`  (Register_IP)
* Caller: full/NewMenu2D_Register.cs:241-283.
* Form (urlencoded): `address=vdarts`, `phone=000`, `gender` (male|female), `account`, `realname`, `nickname`,
  `birthday` (YYYY-M-D from dropdown text), `password`, `nation`, `email`.
* **Any HTTP 2xx = success** (body not read). The body is only parsed when `www.error` is set (i.e. HTTP ≥400):
  body length < 3 → success; `error` == `"01"` account taken, `"02"` nickname taken, `"03"` email taken (string
  compares via `ToString()`, so send JSON strings).
* Success: HTTP 200 `{"status":"1"}`. Failure: HTTP 400 `{"error":"01"}`.

---

## 8. League

### 8.1 Team logos – `GET http://tw.vdarts.net:8000/MemberAPI/download/team/<logo>`
* Caller: full/League_MainScreen.cs:866-899, started in Start (:66) once per process (`LeagueSystem.isDownLoadTeamTexture`).
  `<logo>` = `team1.logo` / `team2.logo` from the league-match detail JSON (LeagueSystem.cs:489/502, API agent).
* On HTTP error it **immediately restarts itself with no delay → infinite tight request loop** (one request at a time,
  forever, while in the league scene). Always answer **200 with an image** for any filename (e.g. a default PNG).

### 8.2 League lists – via API (other agent)
* `LeagueDataCtrl.cs:228/261` raw `t1.vdarts.tw/MemberAPI/leagueMatchList.php` / `leagueMatch.php` are the non-formal
  branch; `DevelopMenu.formalLeagueData = true` → uses `API.GetLeagueMatchList/GetLeagueMatch`. For reference the
  list parser casts `JSON.Parse(res)` to `JSONArray` and reads `id, seaon, name, team_name1, team_name2`; `res.Length <= 3`
  (e.g. `[]`) = "No Data" and is fine.
* Player FX in league: League_MainScreen.cs:719 uses `API.GetFxPersonal` (same JSON as §6.1). The hard-coded
  `http://t1.vdarts.tw/MemberAPI/fxPersonal.php?token=7000...` (:740) is dead.

---

## 9. Software update / version

### 9.1 `GET http://ftp.vdarts.net/download/vdarts-u3d.tar.gz.php`
* Caller: full/UpdateProgress.cs:24-39 (Start of the update-progress widget).
* Response: `{"size": <bytes>}` – `size.AsFloat`, clamped to 50 MB..500 MB, used only to compute a percentage of
  `/opt/vdarts/vdarts-u3d.tar.gz` (the actual download is done by an external Linux updater, not the game).
  `/opt/vdarts/vdarts-u3d.tar.gz.md5.ok` → "finished"; no file → "no update".
* Error → retry every 300 s; display does nothing until success. Not blocking.
* Sample: `{"size":"104857600"}` (any 200 JSON; `{}` also works → size 0 → clamped).

### 9.2 `GET http://api.vdarts.net:8080/VDartsAPIs/version?machine_type=1&version_type=now`
* Caller: full/VersionCheck.cs:43-64, only if `NetCheck.isOnline` (§9.3).
* Response: **plain text** version, e.g. `02.10.04`. Dots stripped, right-padded to 5 digits, `int.Parse`d; if greater
  than local `02.10.04` (→ 21004) a popup offers `OpenURL(.../version/<v>)` and (if `exitGame`, default false) quits.
  Empty body → 0 → no popup. Non-numeric body → exception (harmless).
* **Minimal:** `02.10.04` (or empty 200).

### 9.3 `GET http://www.vdarts.net` (HttpWebRequest, background thread)
* full/NetCheck.cs:17-41 – polls every 15 s forever; `isOnline` = 2xx. Used only to gate §9.2. 200 with any body.

---

## 10. Other WWW uses – no server needed (local files or dead)
* Local `file://` loads only: BustMovie.cs:104-112, TossCoinMovie.cs:74, DynamicMenuBackground.cs:38,
  EffectObject.cs:313, PlayObject.cs:119, MoviePlayer.cs:48, CustomBG.cs:198 (`file:///` + saved background),
  Advertising.cs:142/185, Mall_FourItem.cs:256/292, Mall_ItemDescription.cs:242-256, AnnouncementDescription.cs:237,
  Reporter.cs:1865 (build_info.txt), AdsSystem.cs:146.
* MediaPlayerCtrl.cs:929-1000 `DownloadStreamingVideoAndLoad*` – never called with a remote URL.
* DownloadTexture.cs:13 – NGUI sample (`http://www.yourwebsite.com/logo.png` default); no instance configured in code.
* NewMenu2D.cs:302 `NetworkTest` / NewMenu2D_Base.cs:123 `LinkTest` (`GET https://api.vdarts.net` with the
  cert handler) – never started; `CheckOnline` stays 1.
* OISystem.cs:2827-2835 `IEPUshVPoint` → `113.68.160.41:8001/MemberAPI/vpointAdd.php` – never called (live path is
  `API.VPointAdd`).
* Player logos (`PlayerInfoPool.GetInfo(n,"logo")`, cardPlayerSystem.cs:2028/2174, NewMenu2D_Member.cs:323/547,
  League_MainScreen.cs:706) – arbitrary URL from member info; error → default `p5` texture.
* `NewMenu2D.cs:923 getpicture(url)` – user image from member info URL; error → LoadingDisable.
* Online lobby (fpfull/OnlinePlayer.cs:65) `GET http://api.vdarts.net:8000/MemberAPI/memberGet.php?id=<memberId>`
  (ONLINE_BACKGROUND) – reads `rank.average`, `consume_rank`, `fx` (object; `fx.txt`, `fx.bak`). Error → skipped,
  `afterAction` still runs. Minimal: `{"rank":{"average":"0"},"consume_rank":"","fx":""}`.
  Portraits `http://api.vdarts.net:8080/picture/portrait/<file>` (OnlinePlayer.cs:54). (Possibly overlaps the
  member/online agent.)

---

## 11. Quick stub table

| Endpoint | Method | Minimal 200 body | Matters for |
|---|---|---|---|
| `:8080/VDartsAPIs/coin` | POST | `{}` (any) | **unlocking LockGame** |
| `:8000/MemberAPI/advertisementGet.php` | GET | `[]` | ads; non-array/no-`/ads/` url breaks loop |
| `:8000/MemberAPI/messageGet.php` | GET | one-item `data` (see 4.1) | announcement panel / marquee |
| `:8000/MemberAPI/messageViewLog.php` | GET | `{"status":"1"}` | nothing |
| `:8080/VDartsAPIs/suggestion` | POST multipart | `{}` | feedback popup |
| `:8043/MemberAPI/fxPersonal.php` | GET | `{"personal":[],"used":[],"list":{}}` | player effects |
| `:8043/MemberAPI/fxPersonal.php` | POST | `{"status":"1"}` (no `info`) | mall buy/use |
| `:8043/MemberAPI/fxUsedEdit2.php` | POST | `{"status":"1","expiry_date":"2099-12-31"}` | mall buy bg |
| `:8043/MemberAPI/fxUsedEdit.php` / `fxUsedDel.php` | POST | `{"status":"1"}` | mall bg |
| `:8043/MemberAPI/fxTextEdit.php` / `fxTextDel.php` | POST | `{"status":"1"}` | custom text |
| `:8043/MemberAPI/download/fx/{video,bak}/*` | GET | image or 404 | previews |
| `:8043/MemberAPI/signup.php` | POST | 200 anything | registration |
| `t1.vdarts.net/MemberAPI/versionVpoint.php` | POST | `{}` | nothing |
| `tw.vdarts.net:8000/MemberAPI/download/team/*` | GET | **always an image** | avoids tight retry loop |
| `ftp.vdarts.net/download/vdarts-u3d.tar.gz.php` | GET | `{"size":"0"}` | update % widget |
| `:8080/VDartsAPIs/version` | GET | `02.10.04` (text) | update popup |
| `www.vdarts.net` | GET | 200 | NetCheck gate |
| `:8080/VDartsAPIs/businessLogin`, `businessRegistered` | POST | `{"error":"0"}` | dormant |
