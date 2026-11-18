# vdarts-server: a self-hosted backend for VDarts H2

This is a self-hosted alternative to the online services of **VDarts H2** (Unity 2018.4 Windows build, `VDartsH2_V1.exe`). It is a single self-contained
.NET 8 executable with SQLite storage.

The official servers (`api.vdarts.net`, `online.vdarts.net`) were still answering on 2026-11-17. vdarts-server is for running cabinets against
your own data, offline or on a LAN. It does not replace a dead service, and nothing in it talks to the official servers. The online-lobby
protocol was rebuilt from the client code alone; see `docs/lobby-protocol.md`.

Together with the **VDarts Server Redirect** BepInEx plugin, the game runs fully "online" against your own machine:
- 10-digit member cards log in, show a profile and portrait, and upload game records.
- Rankings, game prices, rounds, announcements and leagues come from your database.
- The **online lobby** ("Online PK") runs on the server's TCP port 8008. Cabinets that point at the same server see each other and can play online games.
- The **effects mall** (hit-effect videos and sounds, backgrounds) and the idle **ads** come from local content. Purchases are free by default, or cost V-points, and are stored per member.

Status, verified on the real game (2026-11-17):
- The network badge shows **Online**, and the marquee shows the server's announcement.
- Typing a 10-digit card on the card screen logs in a member.
- An unknown card is auto-registered (`Player<last4>`).
- A full 2-player 501 game uploaded `gameVdartsRecord.php`, and both players appear in the 501 ranking.
- Lobby: the game and a scripted client saw each other and finished an online 501 through the server, with darts relayed both ways, result screen, and back to the lobby.
- Mall: catalogue, purchase/use/toggle, backgrounds with previews, and ads were served from local content (tested with curl).

```
vdarts-server/          this source (C#, net8.0)
  docs/                 full decompiled endpoint catalogs (request/response details, source refs)
  docs/lobby-protocol.md  online lobby TCP protocol (every message, both directions)
  tools/import-content.ps1  fills data/content (mall catalogue, backgrounds, ads) from local data
vdarts-server-bin/      published single-file binary + data/vdarts.db
mods/VDartsServerRedirect/   BepInEx 5 plugin source (URL redirect)
BepInEx/plugins/VDartsServerRedirect/VDartsServerRedirect.dll   deployed plugin
```

---

## 1. Quick start (Windows, everything on one PC)

1. **Start the server**:
   ```powershell
   cd <game folder>\vdarts-server-bin
   .\vdarts-server.exe
   ```
   It listens on `http://0.0.0.0:8090` and, for the online lobby, on `tcp/8008`. On first run it creates `data\vdarts.db` next to the working directory.
   Optional: fill the mall and ads (§3.1).
2. **Plugin**: BepInEx 5.4.23.2 x64 must be installed. Put `VDartsServerRedirect.dll` in `BepInEx\plugins\VDartsServerRedirect\` (already done here).
3. **Start the game.** `BepInEx\LogOutput.log` should contain:
   - `VDarts Server Redirect ... Patched 33 HTTP entry points`
   - `api.vdarts.net` → `http://127.0.0.1:8090` rewrite lines
4. **Check it works**:
   - The badge in the top-left corner shows *Online*.
   - Choose a game and player count. On the card screen, type any 10-digit number and press Enter; the keyboard-wedge reader does the same.
5. **Manage data** at <http://127.0.0.1:8090/admin> (see §4).

### Running the server on another machine
Run the server anywhere reachable. Then set the game's plugin config `BepInEx\config\net.vdarts.preservation.serverredirect.cfg`:

```ini
[Server]
Enabled = true
BaseUrl = http://192.168.1.50:8090   # where vdarts-server listens; no trailing slash
Domains = vdarts.net,vdarts.tw       # any host under these domains is redirected
LogRequests = true                   # log every rewrite to LogOutput.log
[Machine]
MacOverride =                        # reserved, leave empty
[Lobby]
Enabled = true                       # point the online lobby (ServerIpControl.ONLINE_IP/ONLINE_PORT) at your server
Host =                               # empty = the host of BaseUrl
Port = 8008                          # = the server's Vdarts:LobbyPort
```

Every cabinet that should share a lobby must point at the same server, and TCP 8008 must be reachable (open it in the server's firewall).

Also set `--Vdarts:PublicUrl=http://192.168.1.50:8090` on the server. Otherwise logo URLs are derived from the request's Host header, which is usually fine.

### Building from source
Requires the .NET 8 SDK.

```powershell
cd vdarts-server
dotnet publish -c Release -r win-x64 -o D:\vdarts-server-bin    # or -r linux-x64 / linux-arm64
```

The plugin builds on net452 against the game's own `Managed\` assemblies and copies itself into `BepInEx\plugins\VDartsServerRedirect\`.
Set `VDARTS_GAME_DIR` to the absolute path of the game folder first. The build stops with an error if it is missing:

```powershell
$env:VDARTS_GAME_DIR = 'D:\Games\VDartsH2'
dotnet build mods\VDartsServerRedirect -c Release
```

---

## 2. How the redirect works (and why a plugin rather than a hosts file)

The game uses three HTTP stacks: `UnityEngine.WWW`, `UnityWebRequest`, and `System.Net.WebRequest` (inside `APICore`, for `/MemberAPI/*.php`). The base URLs are:
- `https://api.vdarts.net/MemberAPI`
- `http://api.vdarts.net:8000/MemberAPI`
- `http://api.vdarts.net:8080/VDartsAPIs`
- `/MemberPlatformWS`, plus several hard-coded strings in `ServerIpControl` (firstpass)

How the plugin redirects them:
- It Harmony-patches every URL-taking entry point of the three stacks (33 methods).
- It rewrites the static URL fields of `ServerIpControl`.
- Any `http(s)://*.vdarts.net|vdarts.tw[:port]` prefix is replaced with `BaseUrl`, and **the path is kept**. So one plain-HTTP server on one port answers everything.
- The game's IL is not modified.
- It is independent of `mods/VDartsMod`.

A hosts-file redirect would also work, but you would need listeners on 443 (TLS), 8000, 8043 and 8080. TLS is easy because the client accepts any certificate, but the plugin is simpler.

---

## 3. Server configuration

These are standard ASP.NET Core configuration sources:
- command-line `--Vdarts:Key=value`
- environment `Vdarts__Key=value`
- an `appsettings.json` next to the exe

The listen address is set with `--urls http://0.0.0.0:8090` or `ASPNETCORE_URLS`.

| Key | Default | Meaning |
|---|---|---|
| `Vdarts:Database` | `data/vdarts.db` | SQLite file (WAL mode). Back this file up. |
| `Vdarts:PublicUrl` | *(Host header)* | Base URL put into portrait/logo links. |
| `Vdarts:AutoRegisterCards` | `true` | Unknown 10-digit cards become new members (`Player<last4>`). If `false`, unknown cards get `card_no error`. |
| `Vdarts:AutoRegisterMachines` | `true` | Unknown MACs are added to `machines`. If `false`, they get `machine_mac error`. |
| `Vdarts:DefaultBalance` | `0` | Starting `pay.vch` credit for auto-registered members. |
| `Vdarts:DefaultVpoint` | `0` | Starting V-points. |
| `Vdarts:VpointPerGame` | `1` | V-points added per `vpointAdd` (end of each game). |
| `Vdarts:DefaultCompanyName` | `""` | Dealer / shop name for new machines. It is shown in rankings. |
| `Vdarts:LatestVersion` | `02.10.04` | Returned by `/VDartsAPIs/version`. Keep it ≤ the game's own version so no update prompt appears. |
| `Vdarts:AdminPassword` | `""` | Empty: `/admin` is reachable from localhost only. Set: HTTP Basic auth from anywhere (any user name). |
| `Vdarts:LogRequests` | `true` | Log every request (method, path, first 300 chars of body). Unknown paths are always logged as `UNKNOWN`. |
| `Vdarts:LobbyPort` | `8008` | TCP port of the online lobby. `0` disables it. Lobby traffic is always logged (category `lobby`, login tokens left out). |
| `Vdarts:ContentDir` | `data/content` | Mall and ad content (see §3.1). |
| `Vdarts:MallFree` | `true` | `true`: every mall item costs 0. `false`: the catalogue price is deducted from the member's V-points (`member vpoint insufficient` if too low). |

Example (LAN server with an admin password):

```powershell
.\vdarts-server.exe --urls http://0.0.0.0:8090 --Vdarts:AdminPassword=change-me --Vdarts:DefaultCompanyName="My Bar"
```

### 3.1 Mall and ad content

`ContentDir` layout:

```
fx/fxPersonal_all_<lang>.json   effect catalogue as served by fxPersonal.php?type= (one file per language: en-us, zh-tw, ja-jp ...)
fx/fxGetAllBak.json             background index (key -> uuid)
fx/bak/<uuid>.jpg               background images (a background is offered only if its file exists)
fx/video/<url>.jpg, fx/bak/<url>.png   optional shop previews (a missing .png falls back to the .jpg)
ads/pic/*.jpg|png, ads/video/*.ogv     idle-screen ads (advertisementGet.php lists them all)
```

The effect videos and sounds ship with the game (`StreamingAssets/Effect/fx_.vid.NNN.ogv`, `Audio/fx_.wav.NNN`). The catalogue only unlocks
them. Fill the directory with:

```powershell
cd vdarts-server\tools
.\import-content.ps1 -Archive D:\vdarts\archive\vdarts-net-2026-11-17 -Cabinet D:\vdarts\cabinet\VDartsH2_V1 -Content D:\vdarts-server-bin\data\content
```

The script copies only whitelisted files: catalogue JSON, background/preview images, and ad pictures and videos. It never reads a cabinet's
`GameData\*.dat`, `prefs`, `Player.log` or `setting.txt`, because those hold that cabinet's card numbers and machine identity. With an empty
`ContentDir` the mall shows empty lists and no ads play.

Purchases are stored in `fx_owned`, and choices in `fx_used`, `fx_bg_used` and `fx_text`. Both are per member. Choices appear in
`card_login.fx` and `memberGet.php`, so opponents see them too.

---

## 4. Admin page (`/admin`)

This is a plain HTML page; no JavaScript is needed. It covers:
- **Members**: edit nickname, nation, account/password (for account login), balance (`vch`, `gft`), V-points, lock, and rating overrides (01 / Cricket). Delete card bindings.
- **Cards**: bind more card numbers to a member.
- **Game keys**: staff cards that open the operator settings. Leave the MAC empty to make a card valid on all machines. `2513081404` and `1762817609` are hard-coded in the game and always work.
- **Machines**:
  - name, dealer/company name, dealer password, lock / lock-after date
  - coin counters
  - prices JSON (`gamePriceGet`) and rounds JSON (`gameRoundGet`); the machine's own settings screen writes these too
- **Recent games**: the last uploaded records.
- **Announcements**: marquee text (`messageGet.php`).
- **Leagues**: matches per machine (`leagueMatchList` / `leagueMatch`) and the `leagueTeamRank` JSON.
- **Feedback**: messages sent from the in-game suggestion box.

`GET /admin/api/members` returns the member list as JSON.

---

## 5. Things a server author must know (client quirks)

Full details are in `docs/endpoints-php-memberapi.md` §0.

- **Errors must be HTTP 200 with JSON.** `APICore` treats a non-200 response as an empty result and simply retries or ignores it.
- **Request bodies are not URL-encoded.** Raw JSON arrives in `records=`, `data=`, `list=`, `token=[...]`. The server splits on `&` and the first `=`, and percent-decodes only "plain" values.
- **SimpleJSON (I2.Loc):**
  - Every value is a string; `null` reads as the string `"null"`; an empty string `""` behaves like a *missing key*.
  - The server therefore sends every field as a **non-empty string**, using `"-"` for blanks.
  - Booleans are mostly compared as `"true"`/`"false"`, e.g. `checkKeyCard.is_paid` and `is_locked`. `isValid` in `login.php` is `"1"`.
- **Tokens**:
  - `login.php` returns `game_token`, which **must be exactly 64 characters**. The client hex-encodes it to 128 chars ("Java token") and decodes it again for most calls.
  - `vpointAdd.php` sends the 128-hex form; everything else sends the 64-char form. The server accepts both.
- **Ratings**:
  - The level is a string like `A10` / `N1`. The client takes the first run of digits, and a level below 3 counts as a beginner.
  - Records carry PPD (01 games) or MPR (cricket). This server's formula is an **approximation**: average of the last 30 games, `Level01 = clamp(floor(PPD/3), 1, 18)`, `LevelCr = clamp(floor(MPR*3.3), 1, 18)`. Letter classes: ≤2 N, ≤5 C, ≤8 B, ≤12 A, ≤15 AA, else SA.
  - Admin overrides win.
- **Offline lock**:
  - The game counts time without network, and after a limit it shows `LockGame`. Price item `998` (OfflineLimit) controls it; the server always returns 998 with `pay.coi "0"`, which disables the lock.
  - `POST /VDartsAPIs/coin` answering 200 also releases a lock.
- **The Online/Offline badge** (globe, top left) is not polled. `PublicStuShow` sets it from the result of the last `machineCheckCoin.php` (sent on entering the main menu), card login or `/VDartsAPIs/consume`. If the server was down at that moment, the badge stays *Offline* until the next card login or the next return to the menu, even after the server is back.
- **Daily rankings** are computed locally by the client from its own records. Server rankings (`/VDartsAPIs/gameRanking`) are all-time best per member.
- **Version check**: `/VDartsAPIs/version` must not be newer than the game, otherwise it nags about updates. `/download/vdarts-u3d.tar.gz.php` returns `{"size":"0"}`.

---

## 6. Endpoint list

Legend:
- **Real**: backed by the database.
- **Stub**: returns a response the client accepts, but the feature does nothing; the reason is given.

Paths are as seen by the server after the redirect. Parameters come from the query string and/or the form body (merged). The full request and response details, with decompiled source references, are in `docs/`:
- `docs/endpoints-php-memberapi.md` (37 sections)
- `docs/endpoints-java-vdartsapis.md` (16 sections)
- `docs/endpoints-business-misc.md` (locks, ads, announcements, mall, feedback)

### 6.1 PHP MemberAPI (`/MemberAPI/*.php`; originally `https://api.vdarts.net` and `:8000`)

| Endpoint | Method / params | Response (all values strings) | Status |
|---|---|---|---|
| `echo.php` | GET | `{"cip","time"}`: latency probe | Real |
| `login.php` card mode | POST `card_no, machine_mac, card_login=1, src, system, version` | Success: `{"token","game_token"(64 hex),"uuid","isValid":"1","card_type":"crd.mem","card_login":{profile}}`. Profile = `member_id, account, nickname, nation, logo(URL), email, address, phone, card_no, is_locked, vpoint, vcoin, consume_rank, rank:{2,3,average,league:{average}}, balance:{pay:{vch,gft}}, fx:{txt,bak,dbg,mbg}`. Unknown card: `{"error_msg":"card_no error","token":"-"}`. Unknown machine (auto-register off): `{"error_msg":"machine_mac error"}` | Real |
| `login.php` account mode | POST `account, password, member_login=1, machine_mac` | Same profile under `member_login` | Real |
| `login.php` QR mode | POST `game_token` (polled) | `{"error_msg":"wait"}` forever | **Stub**: the official VDarts phone app pairs QR logins, and it only talks to the official servers |
| `memberToken.php` | `token` | Profile for the token | Real |
| `memberGet.php` | `member_id` / `token` | Profile | Real |
| `machineStatus.php` | GET `mac` (or `machine_name, dealer_psw`) | `{"mac","name","company_name","is_locked","lock_after","server_time"}` | Real |
| `gamePriceGet.php` | GET `mac` | `[{"game":"202","cost":{"pay.gft","pay.vch","pay.coi"}},…]`; always includes `998` with coi `0` | Real |
| `gamePrice.php` | POST `mac, consume_type, …` | stores the machine's price table | Real |
| `gameRoundGet.php` / `gameRound.php` | `mac` / `mac, list=[…]` | `{"round":{game:rounds}}` (or `{}`) / `{"result":"ok"}` | Real |
| `machineCheckCoin.php` | POST `mac, insert, consume` | `{"insert","consume"}`: coin counters (max of stored and sent) | Real |
| `gameVdartsRecord.php` | POST `game_type, group_by, records=[{member_id, machine_mac, rating, score, record[[…]], effect{}, handicap_score, version,…}]` | `{"result":"ok","saved":"N"}`. Records with `member_id ≥ 1000` are stored; every raw upload is kept in `raw_uploads` | Real |
| `gameRecordGetRanking2.php` | `token, game_type` | `{"self":{"rank":"N"}}` | Real |
| `vpointAdd.php` | POST `token`(128 hex)`, action, mac, gameType` | `{"result":"ok"}` (+`VpointPerGame`) | Real |
| `vpointGet.php` | `token` | `{"vpoint"}` | Real |
| `consume.php` | GET `data=[{type, mac, member_id, game, coin}]` | `{"result":"ok"}`: card pays for a game (gft first, then vch) | Real |
| `consumeCardToCoin.php` | POST `token, mac, coin` | `{"success":"1","value":"N"}`, or `{"error":"card_no error"} / {"error":"pay.vch"}`; deducts gft then vch | Real |
| `extPayUrl.php` | GET `mac` | `{"result":"disabled"}` | **Stub**: external payment QR not supported |
| `checkKeyCard.php` | POST `card_no, mac` | `{"data":{"is_paid":"true","is_key_card":"true\|false","qr_code":"-","start_date","end_date":"2099-12-31"}}` | Real (key cards); subscription is always "paid" |
| `leagueMatchList.php` | GET `mac` | Array of matches from `leagues` | Real |
| `leagueMatch.php` | GET `id` / POST `id, data={…}` | Stored match detail / stores the result and merges it into the detail | Real |
| `leagueTeamRank.php` | GET | JSON from the admin page | Real (admin-maintained) |
| `download/team/*` | GET | Default PNG | **Stub**: no team logos |
| `fxPersonal.php` | GET `type=` (+`language`) | Catalogue `[{i18n_index,type,url,price,price_array,target,name,…}]` from `fx/fxPersonal_all_<lang>.json`, filtered by type prefix | Real (content) |
| `fxPersonal.php` | GET `token` [`member_id`] | `{"personal":[{fx,type,expiry_date,target}],"used":[{fx,type,target,addtion}],"list":{}}` for the token's member (or `member_id`); expired items are left out | Real |
| `fxPersonal.php` | POST `action=purchase` `fx, time_val` / `action=used` `fx, gam_val, used_type` | `{"status":"1","expiry_date"}`, or `{"status":"0","info":…}` (`time_val error`, `member vpoint insufficient`, `no purchase`, `passed its expiry_date`, `gam_val error`, `used_type null` …). Using the item already in a slot unequips it | Real |
| `fxGetUsed.php` | GET `token` or `card_no` | `{"txt":{type:text},"bak":{target:{url}},"dbg":{},"mbg":{}}` | Real |
| `fxGetAllBak.php`, `fxGetAllBak2.php` | GET [`token`] | Backgrounds whose image exists: `{key:{url,…}}` / `{"list":{key:{name,url,version,price_array,expiry_date?}}}` | Real (content) |
| `fxUsedEdit(2).php`, `fxUsedDel.php` | POST `token, img/fx, gam_val, type` | Buy, use or remove a background (`bak`/`dbg`/`mbg`) | Real |
| `fxTextEdit.php`, `fxTextDel.php` | POST `token, type=fx_.txt.*, text` (≤ 40 chars) | `{"status":"1"}` | Real |
| `download/fx/bak/*`, `download/fx/video/*` | GET | Background image or preview from `ContentDir/fx`; 404 if missing | Real (content) |
| `lottery.php` | POST `mac, token=[…]` | `{"error":"none"}`: no prize | **Stub**: after-game lottery |
| `supervMachineSchedule.php` | GET/POST | GET `{"dealer":"none"}`, POST `{"info":"success"}` | **Stub**: SuperV tournament scheduling |
| `vpointBet.php` | `type=bet\|fee\|ranking` | `bet`: `{"data":{"is_locked":"1","fee":"0","amount":["10","50","100"]}}`; others empty | **Stub**: staked online games are hidden (`is_locked`). `amount` must hold a non-zero value, or the lobby's 01/Cricket invite dialog throws and never sends the invite |
| `league3MatchList.php`, `league4Match.php` | | `{"data":[]}` / `{"code":"0","msg":"ok","data":{"list":[]}}` | **Stub**: V-Crown online leagues |
| `league4Vteam.php` | `requesttype` | `apply_fee_list` → text `0`; `add_match` → local id | **Stub**: VTeam |
| `machineCheckOnline.php` | | `{"status":"1","data":{"minute":"0"}}` | **Stub**: online-time accounting |
| `cancelAccount.php` | GET/POST | GET timestamp; POST `{"status":"0","error":"del_error"}` | **Stub**: refuse account deletion from the cabinet (use /admin) |
| `advertisementGet.php` | GET `mac` | `[{"url":"<base>/ads/pic/x.jpg"},{"url":"<base>/ads/video/y.ogv"}…]`; every URL contains `/ads/` | Real (content) |
| `messageGet.php` | GET `lang, source` | Announcements from the DB (`media_path "-"`) | Real |
| `messageViewLog.php` | | `{"status":"1"}` | Stub (no-op) |
| `signup.php` | | HTTP 400 `{"error":"01"}` / `{"error":"02"}` | **Stub**: in-game web signup disabled; cards auto-register instead |
| `versionVpoint.php` | | `{"status":"1"}` | Stub |

### 6.2 Java API (`/VDartsAPIs/*`; originally `http://api.vdarts.net:8080`) and `/MemberPlatformWS`

| Endpoint | Method / params | Response | Status |
|---|---|---|---|
| `is_gamekey` | GET `card` (`mac`) | `{"result":"1"\|"0"}` | Real |
| `member/login` | POST `card_no` or `account+password` | `{"isValid":"true","token"(128 hex),…profile}` | Real |
| `member` | GET/PUT, header `WWW-Authenticate: <token>` or `?card=` | Member JSON / update nickname, nation, … | Real |
| `/MemberPlatformWS/member` | same as above | | Real |
| `/MemberPlatformWS/member/accessibility` | GET | text `true`/`false` | Real |
| `/MemberPlatformWS/card` | GET `token` | `[{"no":"<card>"}]` | Real |
| `member/logo` | POST multipart (image) | `{"filepath":"<url>"}` (or `{"error":"no photo"}`); served at `/picture/portrait/<id>.png` | Real |
| `consume` | POST `token, game, coin` | Deducts gft then vch | Real |
| `coin` | POST `mac` | `{"result":"1"}` (unlocks `LockGame`) | Real (always unlocks) |
| `record` | POST | `{"result":"1"}` (stored raw) | Real |
| `record/bundle`, `bundle` | GET | text integer (PK group id) | Real (counter) |
| `gameRanking` | GET `game` (202, 208, 301, 308, 101, 102, 603, 604), `limit` | `[{"rank","nickname","nation","score","dealer","time"}]`. Score games (CountUp/Sniper) rank by best score; 01/Cricket by best PPD/MPR | Real |
| `version` | GET | text `02.10.04` | Real (config) |
| `suggestion` | POST multipart | Stored in `feedback` | Real |
| `fx` | | `{"text":{}}` | **Stub** (mall) |
| `businessLogin`, `businessRegistered`, `businessBox` | POST | Login against `businesses` / register / empty | Real but **dormant**: the client's BusinessPage / BusinessLockSystem is dead code |

### 6.3 Static

| Path | Response |
|---|---|
| `/picture/portrait/<id>.png` | Uploaded logo or a built-in default silhouette, `Content-Type: image/png` |
| `/ads/pic/*`, `/ads/video/*` | Ad files from `ContentDir/ads` |
| `/download/vdarts-u3d.tar.gz.php` | `{"size":"0"}`: no update offered |
| `/` | `vdarts-server OK` |
| anything else | Logged as `UNKNOWN`; returns HTTP 200 `{"result":"0","error":"not implemented"}` |

---

## 7. Online lobby (TCP 8008)

`Lobby.cs` implements the "Online PK" lobby: player list, invites (every game type, plus 01 In/Out/Bull settings), dart and turn relay,
chat phrases, rematch, and disconnect handling. `docs/lobby-protocol.md` documents the protocol as rebuilt from the client.

Facts worth knowing:
- A cabinet joins the lobby when a member card logs in on **Online → Online PK**. Players are listed with nickname and rating.
- The invitee always throws first. Each cabinet scores the game locally from the relayed darts. The server does not check scores.
- If a player leaves or their connection drops, the opponent receives the client's "opponent left" signal. In a free game, that cabinet then finishes against the built-in robot.
- Both cabinets need the plugin's `[Lobby]` section pointing at the same server.
- **Only the invitee's cabinet records an online game.** It uploads `gameVdartsRecord.php` (game_type 208/209 for 501/701) with real
  records for both players. The inviter's cabinet never sets its local player's `member_id` (`onlineMenuSystem.CreateOnlineGame`), so its
  upload holds only the opponent as guest `"9"`, and the server skips that. This is the game's own design, and it avoids counting the
  game twice. A game the opponent abandoned (finished against the robot) is not uploaded at all.

## 8. Not covered

These features answer with stubs. The rest of the game is unaffected.

- **Webcam and microphone relay** in online games, which use separate media servers. Not started.
- **V-point stakes** in online games, **V-Crown / VTeam** leagues, and the after-game **lottery**: stubbed as described above.
- **QR / phone-app login** (`login.php` with `game_token`): the official VDarts phone app does the pairing against the official servers.
- **Software updates**: never offered.

---

## 9. Data

The data is stored in SQLite `data/vdarts.db`. Tables:
- `members` (ids from 1001), `cards` (card_no → member), `tokens`
- `machines`, `game_keys`
- `records`, `raw_uploads`
- `announcements`, `leagues`, `league_results`
- `feedback`, `businesses`, `settings`
- `fx_owned`, `fx_used`, `fx_bg_used`, `fx_text` (mall)

Inspect it with any SQLite tool while the server is running (WAL mode). To start from scratch, stop the server and delete `data/`.

Known test data from the first run (delete it in `/admin` if unwanted):
- Members 1001 (`0012345678`) and 1002 (`4242424242`), with one 501 game. Member 1001 also has a few test mall purchases.
- Member 1003 (`0099990001`), created by the scripted lobby test client.
- One machine record for the PC used in testing.
