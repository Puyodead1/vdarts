using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using VDartsServer;

// ============================================================================================
//  vdarts-server — self-hostable VDarts H2 backend (an alternative to the official vdarts.net servers).
//  A TCP listener (Lobby.cs, port 8008) replaces online.vdarts.net's online lobby.
//  One HTTP listener serves every path the game uses:
//    /MemberAPI/*.php      (originally api.vdarts.net :443 / :8000 / :8043, t1/tw.vdarts.net)
//    /VDartsAPIs/*         (originally api.vdarts.net:8080, "Java" API)
//    /MemberPlatformWS/*   (originally api.vdarts.net:8080)
//    /picture/portrait/*, /download/*, /  (www.vdarts.net / ftp.vdarts.net)
//  See README.md for the endpoint catalogue and which ones are stubs.
// ============================================================================================

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
var opt = new ServerOptions();
builder.Configuration.GetSection("Vdarts").Bind(opt);
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://0.0.0.0:8090");
builder.WebHost.ConfigureKestrel(k => { k.AllowSynchronousIO = true; k.Limits.MaxRequestBodySize = 20 * 1024 * 1024; });

var app = builder.Build();
var log = app.Logger;
var dbPath = Path.GetFullPath(opt.Database);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var db = new Db(dbPath);
var members = new Members(db, opt);
var mall = new Mall(db, opt);
members.FxOf = mall.Used;
var defaultPng = LoadResource("default_portrait.png");
log.LogInformation("vdarts-server database: {db}", dbPath);

// --------------------------------------------------------------------- helpers
IResult J(JsonNode n) => Results.Text(Util.Json(n), "application/json; charset=utf-8");
IResult T(string s) => Results.Text(s, "text/plain; charset=utf-8");
IResult Png(byte[] b) => Results.Bytes(b, "image/png");
string BaseUrl(HttpContext c) => string.IsNullOrEmpty(opt.PublicUrl) ? $"{c.Request.Scheme}://{c.Request.Host}" : opt.PublicUrl.TrimEnd('/');
string Mac(Req r) => (r["mac"] ?? r["machine_mac"] ?? r["box_mac"] ?? r["device"] ?? "").Trim().ToUpperInvariant();

var routes = new Dictionary<string, Func<Req, IResult>>(StringComparer.OrdinalIgnoreCase);
void Route(string path, Func<Req, IResult> h) => routes[path] = h;

// ============================================================ PHP "MemberAPI"
Route("/MemberAPI/echo.php", r => J(new JsonObject { ["cip"] = r.Ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "127.0.0.1", ["time"] = Util.Now() }));

Route("/MemberAPI/login.php", r =>
{
    var mac = Mac(r);
    Dictionary<string, object> m = null; string key = "card_login";
    if (r["card_login"] == "1" || (r["card_no"] ?? "") != "")
    {
        var card = (r["card_no"] ?? "").Trim();
        if (members.Machine(mac, r.Ip) == null && mac != "") return J(new JsonObject { ["error_msg"] = "machine_mac error" });
        m = members.ByCardOrRegister(card);
        if (m == null) return J(new JsonObject { ["error_msg"] = "card_no error", ["token"] = "-" });
        log.LogInformation("card login {card} -> member {id} ({nick}) on {mac}", card, m["id"], m["nickname"], mac);
    }
    else if (r["member_login"] == "1")
    {
        key = "member_login";
        m = members.ByAccount((r["account"] ?? "").Trim());
        if (m == null || Util.S(m["password"]) == "" || Util.S(m["password"]) != (r["password"] ?? ""))
            return J(new JsonObject { ["error_msg"] = "account or password error", ["isValid"] = "0", ["token"] = "-" });
    }
    else if ((r["game_token"] ?? "") != "")
    {
        // QR login: the phone app would bind the client's GUID to an account. No phone app exists → never completes.
        return J(new JsonObject { ["error_msg"] = "wait" });
    }
    else return J(new JsonObject { ["error_msg"] = "params error" });

    if (Util.L(m["is_locked"]) != 0) return J(new JsonObject { ["error_msg"] = "card locked", ["token"] = "-" });
    var token = members.IssueToken(Util.L(m["id"]), mac);
    return J(new JsonObject
    {
        ["token"] = token,
        ["game_token"] = token, // MUST be exactly 64 chars (client hex-encodes it)
        ["uuid"] = "vdarts-" + Util.S(m["id"]),
        ["isValid"] = "1",
        ["card_type"] = "crd.mem",
        [key] = members.Profile(m, BaseUrl(r.Ctx)),
    });
});

Route("/MemberAPI/memberToken.php", r =>
{
    var m = members.ByToken(r["token"]);
    return J(m == null ? new JsonObject { ["error"] = "invalid_token" } : new JsonObject { ["token"] = Util.NormToken(r["token"]), ["member_id"] = Util.S(m["id"]) });
});

Route("/MemberAPI/memberGet.php", r =>
{
    var m = members.ById(Util.L(r["id"] ?? r["member_id"]));
    return m == null ? J(new JsonObject { ["error"] = "member not found" }) : J(members.Profile(m, BaseUrl(r.Ctx)));
});

Route("/MemberAPI/machineStatus.php", r =>
{
    Dictionary<string, object> mc;
    if ((r["machine_name"] ?? "") != "")
    {
        mc = db.Row("SELECT * FROM machines WHERE name=$1 AND dealer_psw=$2 AND dealer_psw<>''", r["machine_name"], r["dealer_psw"] ?? "");
        if (mc == null) return J(new JsonObject { ["error"] = "invalid mac" });
    }
    else
    {
        mc = members.Machine(Mac(r), r.Ip);
        if (mc == null) return J(new JsonObject { ["error"] = "invalid mac" });
    }
    return J(new JsonObject
    {
        ["mac"] = Util.S(mc["mac"]),
        ["name"] = Util.S(mc["name"]),
        ["company_name"] = Util.S(mc["company_name"]),
        ["is_locked"] = Util.L(mc["is_locked"]) != 0 ? "true" : "false",
        ["lock_after"] = Util.S(mc["lock_after"]),
        ["server_time"] = Util.Now(),
    });
});

Route("/MemberAPI/gamePriceGet.php", r =>
{
    var mc = members.Machine(Mac(r), r.Ip);
    var arr = new JsonArray();
    var prices = Util.TryJson(mc == null ? null : Util.S(mc["prices"])) as JsonObject ?? new JsonObject();
    // 998 = OfflineLimit: column [2] is the offline-lock timeout in minutes; < 10 disables the 48h lock.
    if (!prices.ContainsKey("998")) prices["998"] = new JsonObject { ["pay.gft"] = "0", ["pay.vch"] = "0", ["pay.coi"] = "0" };
    foreach (var (game, cost) in prices) arr.Add(new JsonObject { ["game"] = game, ["cost"] = cost?.DeepClone() });
    return J(arr);
});

Route("/MemberAPI/gamePrice.php", r =>
{
    var mc = members.Machine(Mac(r), r.Ip);
    if (mc == null) return J(new JsonObject { ["error"] = "invalid mac" });
    var prices = Util.TryJson(Util.S(mc["prices"])) as JsonObject ?? new JsonObject();
    var type = string.IsNullOrEmpty(r["consume_type"]) ? "pay.coi" : r["consume_type"];
    if (Util.TryJson(r["list"]) is JsonArray list)
        foreach (var it in list)
        {
            var g = it?["game"]?.ToString(); if (string.IsNullOrEmpty(g)) continue;
            var cost = prices[g] as JsonObject ?? new JsonObject { ["pay.gft"] = "2", ["pay.vch"] = "2", ["pay.coi"] = "2" };
            cost[type] = it["price"]?.ToString() ?? "2";
            prices[g] = cost;
        }
    db.Exec("UPDATE machines SET prices=$1 WHERE mac=$2", Util.Json(prices), Util.S(mc["mac"]));
    return J(new JsonObject { ["result"] = "ok" });
});

Route("/MemberAPI/gameRoundGet.php", r =>
{
    var mc = members.Machine(Mac(r), r.Ip);
    var rounds = Util.TryJson(mc == null ? null : Util.S(mc["rounds"])) as JsonObject;
    return J(rounds == null || rounds.Count == 0 ? new JsonObject() : new JsonObject { ["round"] = rounds });
});

Route("/MemberAPI/gameRound.php", r =>
{
    var mc = members.Machine(Mac(r), r.Ip);
    if (mc == null) return J(new JsonObject { ["error"] = "invalid mac" });
    var rounds = Util.TryJson(Util.S(mc["rounds"])) as JsonObject ?? new JsonObject();
    if (Util.TryJson(r["list"]) is JsonArray list)
        foreach (var it in list)
        {
            var g = it?["game"]?.ToString(); var n = it?["round"]?.ToString();
            if (int.TryParse(g, out _) && int.TryParse(n, out _)) rounds[g] = n;
        }
    db.Exec("UPDATE machines SET rounds=$1 WHERE mac=$2", Util.Json(rounds), Util.S(mc["mac"]));
    return J(new JsonObject { ["result"] = "ok" });
});

Route("/MemberAPI/machineCheckCoin.php", r =>
{
    var mc = members.Machine(Mac(r), r.Ip);
    if (mc == null) return J(new JsonObject { ["error"] = "invalid mac" });
    long ins = Math.Max(Util.L(r["insert"]), Util.L(mc["coin_insert"]));
    long con = Math.Max(Util.L(r["consume"]), Util.L(mc["coin_consume"]));
    db.Exec("UPDATE machines SET coin_insert=$1, coin_consume=$2 WHERE mac=$3", ins, con, Util.S(mc["mac"]));
    return J(new JsonObject { ["insert"] = Util.S(ins), ["consume"] = Util.S(con) });
});

Route("/MemberAPI/gameVdartsRecord.php", r =>
{
    db.Exec("INSERT INTO raw_uploads(path, body, created) VALUES($1,$2,$3)", r.Path, r.RawBody, Util.Now());
    long gameType = Util.L(r["game_type"]);
    var recs = Util.TryJson(r["records"]) as JsonArray;
    if (recs == null) return J(new JsonObject { ["result"] = "ok", ["saved"] = "0" });
    long winner = Util.L(r["winner"]); int idx = 0, saved = 0;
    foreach (var rec in recs)
    {
        idx++;
        long mid = Util.L(rec?["member_id"]?.ToString());
        if (mid < 1000) continue; // "9" = guest
        double rating = double.TryParse(rec?["rating"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        db.Exec(@"INSERT INTO records(game_type, group_by, member_id, mac, rating, score, effect, record, league_id, winner, is_winner, created)
                  VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)",
            gameType, Util.L(r["group_by"]), mid, rec?["machine_mac"]?.ToString() ?? "", rating, Util.L(rec?["score"]?.ToString()),
            rec?["effect"]?.ToJsonString() ?? "{}", rec?["record"]?.ToJsonString() ?? "[]", r["league_id"] ?? "", winner, winner == idx ? 1 : 0, Util.Now());
        saved++;
    }
    log.LogInformation("record upload game {g}: {n} member records", gameType, saved);
    return J(new JsonObject { ["result"] = "ok", ["saved"] = Util.S(saved) });
});

Route("/MemberAPI/gameRecordGetRanking2.php", r =>
{
    var m = members.ByToken(r["token"]);
    long game = Util.L(r["game"]);
    if (m == null) return J(new JsonObject { ["self"] = new JsonObject { ["rank"] = "-" } });
    var list = Rankings.Board(db, game, 100000);
    var pos = list.FindIndex(e => e.MemberId == Util.L(m["id"]));
    return J(new JsonObject { ["self"] = new JsonObject { ["rank"] = pos < 0 ? "-" : (pos + 1).ToString() } });
});

Route("/MemberAPI/vpointAdd.php", r =>
{
    var m = members.ByToken(r["token"]);
    if (m != null && opt.VpointPerGame > 0) db.Exec("UPDATE members SET vpoint=vpoint+$1 WHERE id=$2", opt.VpointPerGame, m["id"]);
    return J(new JsonObject { ["result"] = "ok" });
});

Route("/MemberAPI/vpointGet.php", r =>
{
    var m = members.ByToken(r["token"]);
    return J(new JsonObject { ["vpoint"] = Util.S(m == null ? 0 : Util.L(m["vpoint"])) });
});

Route("/MemberAPI/consume.php", r =>
{
    db.Exec("INSERT INTO raw_uploads(path, body, created) VALUES($1,$2,$3)", r.Path, r["data"] ?? r.RawBody, Util.Now());
    return J(new JsonObject { ["result"] = "ok" });
});

Route("/MemberAPI/consumeCardToCoin.php", r =>
{
    var m = members.ByCard((r["card_no"] ?? "").Trim());
    long v = Util.L(r["value"]);
    if (m == null || v <= 0) return J(new JsonObject { ["error"] = "card_no error" });
    long vch = Util.L(m["vch"]), gft = Util.L(m["gft"]);
    if (vch + gft < v) return J(new JsonObject { ["error"] = "pay.vch" });
    long fromG = Math.Min(gft, v);
    db.Exec("UPDATE members SET gft=gft-$1, vch=vch-$2 WHERE id=$3", fromG, v - fromG, m["id"]);
    return J(new JsonObject { ["success"] = "1", ["value"] = Util.S(v) });
});

Route("/MemberAPI/extPayUrl.php", r => J(new JsonObject { ["result"] = "disabled" })); // STUB: no external QR payment
Route("/MemberAPI/checkKeyCard.php", r =>
{
    // Settings > Details "subscription" panel. The client bool.TryParse()s is_paid / is_key_card and compares
    // is_paid with the literal "true" → must be "true"/"false", not "1". No subscriptions here: always active.
    var card = (r["card_no"] ?? "").Trim();
    return J(new JsonObject { ["data"] = new JsonObject { ["is_paid"] = "true", ["is_key_card"] = members.IsGameKey(card, Mac(r)) ? "true" : "false", ["qr_code"] = "-", ["start_date"] = "2020-01-01", ["end_date"] = "2099-12-31" } });
});

// ---- leagues (admin-provided data, results stored)
Route("/MemberAPI/leagueMatchList.php", r =>
{
    var arr = new JsonArray();
    foreach (var l in db.Query("SELECT * FROM leagues WHERE mac='' OR mac=$1 ORDER BY id", Mac(r)))
        arr.Add(new JsonObject { ["id"] = Util.S(l["id"]), ["seaon"] = Util.S(l["season"]), ["season"] = Util.S(l["season"]), ["name"] = Util.S(l["name"]), ["team_name1"] = Util.S(l["team_name1"]), ["team_name2"] = Util.S(l["team_name2"]) });
    return J(arr);
});
Route("/MemberAPI/leagueMatch.php", r =>
{
    if (r.Method == "POST" && (r["data"] ?? "") != "")
    {
        db.Exec("INSERT INTO league_results(league_id, mac, data, created) VALUES($1,$2,$3,$4)", r["id"] ?? "", Mac(r), r["data"], Util.Now());
        var res = Util.TryJson(r["data"]);
        var l = db.Row("SELECT * FROM leagues WHERE id=$1", Util.L(r["id"]?.Trim('"')));
        if (l != null && res is JsonObject ro && Util.TryJson(Util.S(l["detail"])) is JsonObject det)
        {   // merge top level result fields (score1/score2/win/nowGameCount/match_detail...) into the stored detail
            foreach (var (k, v) in ro) det[k] = v?.DeepClone();
            db.Exec("UPDATE leagues SET detail=$1 WHERE id=$2", Util.Json(det), l["id"]);
        }
        return J(new JsonObject { ["result"] = "ok" });
    }
    var lg = db.Row("SELECT * FROM leagues WHERE id=$1", Util.L(r["id"]?.Trim('"')));
    if (lg == null) return J(new JsonObject { ["error"] = "no data" });
    var d = Util.TryJson(Util.S(lg["detail"])) as JsonObject ?? new JsonObject();
    d["id"] ??= Util.S(lg["id"]); d["season"] ??= Util.S(lg["season"]); d["name"] ??= Util.S(lg["name"]);
    return J(d);
});
Route("/MemberAPI/leagueTeamRank.php", r => J(Util.TryJson(db.Setting("leagueTeamRank")) ?? new JsonObject { ["list"] = new JsonArray() }));
Route("/MemberAPI/download/team/*", r => Png(defaultPng)); // team logos — must ALWAYS be an image (client retries in a tight loop otherwise)

// ---- personal effects / mall (Mall.cs; catalogue + images from Vdarts:ContentDir)
IResult NeedMember(Func<Dictionary<string, object>, JsonObject> f, Req r)
{
    var m = members.ByToken(r["token"]);
    return m == null ? J(new JsonObject { ["status"] = "0", ["info"] = "token error" }) : J(f(m));
}
Route("/MemberAPI/fxPersonal.php", r =>
{
    if (r.Method == "POST")
        return (r["action"] ?? "") switch
        {
            "purchase" => NeedMember(m => mall.PurchaseEffect(m, r), r),
            "used" => NeedMember(m => mall.UseEffect(m, r), r),
            _ => J(new JsonObject { ["status"] = "0", ["info"] = "action error" }),
        };
    var type = r["type"] ?? "";
    if (type != "")
    {
        var cat = mall.Catalog(r["language"]);
        return J(type == "all" ? cat : new JsonArray(cat.OfType<JsonObject>().Where(x => (x["type"]?.ToString() ?? "").StartsWith(type, StringComparison.Ordinal)).Select(x => (JsonNode)x.DeepClone()).ToArray()));
    }
    if (long.TryParse(r["member_id"], out var other) && other > 0) return J(mall.Personal(other)); // opponent's effects (online games)
    var me = members.ByToken(r["token"]);
    return J(mall.Personal(me == null ? 0 : Util.L(me["id"])));
});
Route("/MemberAPI/fxGetUsed.php", r =>
{
    var m = members.ByToken(r["token"]) ?? (string.IsNullOrEmpty(r["card_no"]) ? null : members.ByCard(r["card_no"].Trim()));
    return J(mall.Used(m == null ? 0 : Util.L(m["id"])));
});
Route("/MemberAPI/fxGetAllBak.php", r =>
{
    var o = new JsonObject();
    foreach (var (k, v) in mall.Backgrounds()) o[k] = v;
    return o.Count == 0 ? T("{  }") : J(o); // "{  }": >3 chars and no keys = nothing to download
});
Route("/MemberAPI/fxGetAllBak2.php", r =>
{
    var m = members.ByToken(r["token"]);
    return J(mall.AllBackgrounds(m == null ? 0 : Util.L(m["id"])));
});
Route("/MemberAPI/fxUsedEdit2.php", r => NeedMember(m => mall.PurchaseBackground(m, r), r));
Route("/MemberAPI/fxUsedEdit.php", r => NeedMember(m => mall.UseBackground(m, r), r));
Route("/MemberAPI/fxUsedDel.php", r => NeedMember(m => mall.RemoveBackground(m, r), r));
Route("/MemberAPI/fxTextEdit.php", r => NeedMember(m => mall.SetText(m, r, false), r));
Route("/MemberAPI/fxTextDel.php", r => NeedMember(m => mall.SetText(m, r, true), r));
IResult ServeContent(string relDir, string name)
{
    var p = mall.File_(relDir, name);
    if (p == null) return Results.NotFound();
    var b = File.ReadAllBytes(p);
    return Results.Bytes(b, Mall.MimeOf(p, b));
}
Route("/MemberAPI/download/fx/bak/*", r => ServeContent("fx/bak", Path.GetFileName(r.Path)));     // in-game background download + mall preview (.png → .jpg)
Route("/MemberAPI/download/fx/video/*", r => ServeContent("fx/video", Path.GetFileName(r.Path))); // effect preview
Route("/MemberAPI/download/fx/*", r => Results.NotFound());
Route("/ads/pic/*", r => ServeContent("ads/pic", Path.GetFileName(r.Path)));
Route("/ads/video/*", r => ServeContent("ads/video", Path.GetFileName(r.Path)));

// ---- misc PHP
Route("/MemberAPI/lottery.php", r => J(new JsonObject { ["error"] = "none" })); // STUB: no prize draw
Route("/MemberAPI/supervMachineSchedule.php", r => r.Method == "GET" ? J(new JsonObject { ["dealer"] = "none" }) : J(new JsonObject { ["info"] = "success" }));
Route("/MemberAPI/vpointBet.php", r => (r["type"] ?? "") switch // STUB: online vpoint betting disabled
{
    // is_locked=1 hides staked games, but "amount" must still hold a non-zero value: the lobby's 01/Cricket invite
    // dialog indexes StakeVpoint_betArray[0] (built only from non-zero amounts) and throws if it was never built.
    "bet" => J(new JsonObject { ["data"] = new JsonObject { ["is_locked"] = "1", ["fee"] = "0", ["amount"] = new JsonArray("10", "50", "100") } }),
    "fee" => J(new JsonObject { ["data"] = new JsonObject { ["fee"] = "0" } }),
    "ranking" => J(new JsonObject { ["data"] = new JsonArray() }),
    _ => J(new JsonObject { ["error"] = "disabled" }),
});
Route("/MemberAPI/league3MatchList.php", r => r.Method == "GET" ? J(new JsonObject { ["data"] = new JsonArray() }) : J(new JsonObject { ["result"] = "ok" })); // STUB V-Crown
Route("/MemberAPI/league4Match.php", r => J(new JsonObject { ["code"] = "0", ["msg"] = "ok", ["data"] = new JsonObject { ["list"] = new JsonArray() } })); // STUB V-Crown double
Route("/MemberAPI/league4Vteam.php", r => (r["requesttype"] ?? "") switch // STUB VTeam: free entry, local match ids
{
    "apply_fee_list" => T("0"),
    "add_match" => J(new JsonObject { ["message"] = "ok", ["data"] = Util.S(db.Insert("INSERT INTO raw_uploads(path, body, created) VALUES($1,$2,$3)", r.Path, r.RawBody, Util.Now())) }),
    _ => J(new JsonObject { ["message"] = "ok" }),
});
Route("/MemberAPI/machineCheckOnline.php", r => r.Method == "GET" ? J(new JsonObject { ["status"] = "1", ["data"] = new JsonObject { ["minute"] = "0" } }) : J(new JsonObject { ["status"] = "1" }));
Route("/MemberAPI/cancelAccount.php", r => r.Method == "GET"
    ? J(new JsonObject { ["status"] = "1", ["data"] = new JsonObject { ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() } })
    : J(new JsonObject { ["status"] = "0", ["error"] = "del_error" })); // STUB: deleting accounts from a cabinet is not supported
Route("/MemberAPI/advertisementGet.php", r => J(mall.Ads(BaseUrl(r.Ctx)))); // top-level array; every url must contain "/ads/"
Route("/MemberAPI/messageGet.php", r =>
{
    var data = new JsonArray();
    foreach (var a in db.Query("SELECT * FROM announcements ORDER BY id DESC LIMIT 20"))
        data.Add(new JsonObject { ["message_id"] = Util.S(a["id"]), ["title"] = Util.S(a["title"]), ["content"] = Util.S(a["content"]), ["update_time"] = Util.S(a["update_time"]), ["is_read"] = "1", ["media_data"] = new JsonArray() });
    if (data.Count == 0) data.Add(new JsonObject { ["message_id"] = "0", ["title"] = "VDarts", ["content"] = "-", ["update_time"] = Util.Now(), ["is_read"] = "1", ["media_data"] = new JsonArray() });
    return J(new JsonObject { ["media_path"] = "-", ["data"] = data });
});
Route("/MemberAPI/messageViewLog.php", r => J(new JsonObject { ["status"] = "1" }));
Route("/MemberAPI/signup.php", r =>
{
    var acc = (r["account"] ?? "").Trim(); var nick = (r["nickname"] ?? "").Trim();
    if (acc == "" || members.ByAccount(acc) != null) return Results.Text("{\"error\":\"01\"}", "application/json", statusCode: 400);
    if (nick == "" || db.Scalar("SELECT 1 FROM members WHERE nickname=$1", nick) != null) return Results.Text("{\"error\":\"02\"}", "application/json", statusCode: 400);
    members.CreateMember(null, acc, r["password"] ?? "", nick, r["nation"] ?? "TW", r["email"] ?? "");
    return J(new JsonObject { ["status"] = "1" });
});
Route("/MemberAPI/versionVpoint.php", r => J(new JsonObject { ["status"] = "1" }));

// ============================================================ "Java" VDartsAPIs (:8080)
Route("/VDartsAPIs/is_gamekey", r => J(new JsonObject { ["result"] = members.IsGameKey((r["card"] ?? "").Trim(), Mac(r)) ? "1" : "0" }));

Route("/VDartsAPIs/member/login", r =>
{
    Dictionary<string, object> m;
    if ((r["card_no"] ?? "") != "") m = members.ByCardOrRegister(r["card_no"].Trim());
    else
    {
        m = members.ByAccount((r["account"] ?? "").Trim());
        if (m != null && (Util.S(m["password"]) == "" || Util.S(m["password"]) != (r["password"] ?? ""))) m = null;
    }
    if (m == null || Util.L(m["is_locked"]) != 0) return J(new JsonObject { ["isValid"] = "false", ["token"] = "-" });
    // Java-side tokens are the 128-hex form of the 64-char PHP token.
    return J(new JsonObject { ["isValid"] = "true", ["token"] = Util.ToJavaToken(members.IssueToken(Util.L(m["id"]), Mac(r))) });
});

IResult MemberGet(Req r)
{
    Dictionary<string, object> m = null;
    if ((r["card"] ?? "") != "") m = members.ByCard(r["card"].Trim());
    m ??= members.ByToken(r.Ctx.Request.Headers["WWW-Authenticate"].ToString());
    if (m == null) return Results.Text("{\"error\":\"unauthorized\"}", "application/json", statusCode: 401);
    if (r.Method == "PUT")
    {
        if ((r["nickname"] ?? "") != "") db.Exec("UPDATE members SET nickname=$1 WHERE id=$2", r["nickname"], m["id"]);
        if ((r["nation"] ?? "") != "") db.Exec("UPDATE members SET nation=$1 WHERE id=$2", r["nation"], m["id"]);
        return Results.NoContent();
    }
    return J(members.Profile(m, BaseUrl(r.Ctx)));
}
Route("/VDartsAPIs/member", MemberGet);
Route("/MemberPlatformWS/member", MemberGet);
Route("/MemberPlatformWS/member/accessibility", r =>
    T((r["field"] ?? "") == "nickname" && db.Scalar("SELECT 1 FROM members WHERE nickname=$1", r["value"] ?? "") == null ? "true" : "false"));
Route("/MemberPlatformWS/card", r =>
{
    var m = members.ByToken(r.Ctx.Request.Headers["WWW-Authenticate"].ToString());
    var arr = new JsonArray();
    if (m != null) foreach (var c in db.Query("SELECT card_no FROM cards WHERE member_id=$1", m["id"])) arr.Add(new JsonObject { ["no"] = Util.S(c["card_no"]) });
    return J(arr);
});

Route("/VDartsAPIs/member/logo", r =>
{
    var m = members.ByToken(r.Ctx.Request.Headers["WWW-Authenticate"].ToString()) ?? members.ById(Util.L(r["id"]));
    var f = r.Files?.GetFile("photo");
    if (m == null || f == null || f.Length < 16) return J(new JsonObject { ["error"] = "no photo" });
    using var ms = new MemoryStream(); f.CopyTo(ms);
    db.Exec("UPDATE members SET logo=$1, has_logo=1 WHERE id=$2", ms.ToArray(), m["id"]);
    return J(new JsonObject { ["filepath"] = members.LogoUrl(m, BaseUrl(r.Ctx)) });
});

Route("/VDartsAPIs/consume", r =>
{
    // members=<128-hex token>[,<token>...], game=<GameType>; deduct the card price (gft first, then vch) like the client does.
    long game = Util.L(r["game"]);
    var mc = members.Machine(Mac(r), r.Ip);
    var prices = Util.TryJson(mc == null ? null : Util.S(mc["prices"])) as JsonObject;
    foreach (var tok in (r["members"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var m = members.ByToken(tok); if (m == null) continue;
        long gftPrice = Util.L(prices?[game.ToString()]?["pay.gft"]?.ToString(), 2), vchPrice = Util.L(prices?[game.ToString()]?["pay.vch"]?.ToString(), 2);
        if (Util.L(m["gft"]) >= gftPrice) db.Exec("UPDATE members SET gft=gft-$1 WHERE id=$2", gftPrice, m["id"]);
        else if (Util.L(m["vch"]) >= vchPrice) db.Exec("UPDATE members SET vch=vch-$1 WHERE id=$2", vchPrice, m["id"]);
    }
    return J(new JsonObject { ["result"] = "1" });
});
Route("/VDartsAPIs/coin", r => { members.Machine(Mac(r), r.Ip); return J(new JsonObject { ["result"] = "1" }); }); // unlocks LockGame
Route("/VDartsAPIs/record/bundle", r => T(Util.S(db.Insert("INSERT INTO raw_uploads(path, body, created) VALUES($1,'',$2)", r.Path, Util.Now()))));
Route("/VDartsAPIs/bundle", r => T(Util.S(db.Insert("INSERT INTO raw_uploads(path, body, created) VALUES($1,'',$2)", r.Path, Util.Now()))));
Route("/VDartsAPIs/record", r => { db.Exec("INSERT INTO raw_uploads(path, body, created) VALUES($1,$2,$3)", r.Path, r.RawBody, Util.Now()); return J(new JsonObject { ["result"] = "1" }); });
Route("/VDartsAPIs/gameRanking", r =>
{
    var arr = new JsonArray(); int i = 0;
    foreach (var e in Rankings.Board(db, Util.L(r["game"]), 100))
        arr.Add(new JsonObject { ["rank"] = (++i).ToString(), ["nickname"] = e.Nickname, ["nation"] = e.Nation, ["score"] = e.Score, ["dealer"] = e.Dealer == "" ? "-" : e.Dealer, ["time"] = Util.Now() });
    return J(arr);
});
Route("/VDartsAPIs/version", r => T(opt.LatestVersion));
Route("/VDartsAPIs/suggestion", r =>
{
    byte[] photo = null; var f = r.Files?.GetFile("photo");
    if (f != null && f.Length > 1) { using var ms = new MemoryStream(); f.CopyTo(ms); photo = ms.ToArray(); }
    db.Exec("INSERT INTO feedback(member_id, type, opinion, photo, mac, created) VALUES($1,$2,$3,$4,$5,$6)", r["id"] ?? "", r["type"] ?? "", r["opinion"] ?? "", photo, Mac(r), Util.Now());
    return J(new JsonObject { ["result"] = "0" });
});
Route("/VDartsAPIs/fx", r => J(new JsonObject { ["text"] = new JsonObject() }));
Route("/VDartsAPIs/businessLogin", r =>
{
    var b = db.Row("SELECT * FROM businesses WHERE acc=$1", r["acc"] ?? "");
    if (b == null || Util.S(b["pass"]) != (r["pass"] ?? "")) return J(new JsonObject { ["error"] = "1" });
    if (Util.S(b["box_mac"]) != "" && !string.Equals(Util.S(b["box_mac"]), Mac(r), StringComparison.OrdinalIgnoreCase)) return J(new JsonObject { ["error"] = "2" });
    db.Exec("UPDATE businesses SET box_mac=$1 WHERE acc=$2", Mac(r), b["acc"]);
    return J(new JsonObject { ["error"] = "0" });
});
Route("/VDartsAPIs/businessRegistered", r =>
{
    if ((r["acc"] ?? "") == "" || db.Row("SELECT 1 FROM businesses WHERE acc=$1", r["acc"]) != null) return J(new JsonObject { ["error"] = "1" });
    if ((r["email"] ?? "") != "" && db.Row("SELECT 1 FROM businesses WHERE email=$1", r["email"]) != null) return J(new JsonObject { ["error"] = "2" });
    if (db.Row("SELECT 1 FROM businesses WHERE box_mac=$1 AND box_mac<>''", Mac(r)) != null) return J(new JsonObject { ["error"] = "3" });
    db.Exec("INSERT INTO businesses(acc, pass, email, box_mac, created) VALUES($1,$2,$3,$4,$5)", r["acc"], r["pass"] ?? "", r["email"] ?? "", Mac(r), Util.Now());
    return J(new JsonObject { ["error"] = "0" });
});
Route("/VDartsAPIs/businessBox", r => J(new JsonObject { ["error"] = "0" }));

// ============================================================ static-ish
Route("/picture/portrait/*", r =>
{
    var name = Path.GetFileNameWithoutExtension(r.Path);
    if (long.TryParse(name, out var id) && db.Scalar("SELECT logo FROM members WHERE id=$1 AND has_logo=1", id) is byte[] b && b.Length > 0)
        return Results.Bytes(b, b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? "image/jpeg" : "image/png");
    return Png(defaultPng);
});
Route("/download/vdarts-u3d.tar.gz.php", r => J(new JsonObject { ["size"] = "0" })); // ftp.vdarts.net updater progress
Route("/", r => T("vdarts-server OK"));

Admin.Map(app, db, members, opt);
if (opt.LobbyPort > 0) new Lobby(members, opt, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("lobby")).Start(app.Lifetime.ApplicationStopping);

// ============================================================ dispatcher
app.Run(async ctx =>
{
    var path = ctx.Request.Path.Value ?? "/";
    var req = await Req.Read(ctx);
    if (path.Equals("/admin", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase)) { await Admin.Handle(ctx, req); return; }
    Func<Req, IResult> h = null;
    if (!routes.TryGetValue(path.TrimEnd('/') == "" ? "/" : path.TrimEnd('/'), out h))
    {
        // wildcard routes ("/x/y/*") — longest prefix wins
        h = routes.Where(kv => kv.Key.EndsWith("/*") && path.StartsWith(kv.Key[..^1], StringComparison.OrdinalIgnoreCase))
                  .OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
    }
    IResult res;
    if (h == null)
    {
        log.LogWarning("UNKNOWN {m} {p}{q} {b}", ctx.Request.Method, path, ctx.Request.QueryString, Trunc(req.RawBody));
        res = J(new JsonObject { ["result"] = "0", ["error"] = "not implemented" });
    }
    else
    {
        if (opt.LogRequests) log.LogInformation("{m} {p}{q} {b}", ctx.Request.Method, path, ctx.Request.QueryString, Trunc(req.RawBody));
        try { res = h(req); }
        catch (Exception e) { log.LogError(e, "handler {p}", path); res = J(new JsonObject { ["result"] = "0", ["error"] = "server error" }); }
    }
    await res.ExecuteAsync(ctx);
});

app.Run();

static string Trunc(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 300 ? s[..300] + "…" : s);

static byte[] LoadResource(string name)
{
    var asm = Assembly.GetExecutingAssembly();
    var rn = asm.GetManifestResourceNames().First(n => n.EndsWith(name));
    using var s = asm.GetManifestResourceStream(rn)!; using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
}

// ============================================================ types
public sealed class ServerOptions
{
    public string Database { get; set; } = "data/vdarts.db";
    /// <summary>Absolute base URL put into logo links. Empty = derive from the request's Host header.</summary>
    public string PublicUrl { get; set; } = "";
    public bool AutoRegisterCards { get; set; } = true;
    public bool AutoRegisterMachines { get; set; } = true;
    public long DefaultBalance { get; set; } = 0;
    public long DefaultVpoint { get; set; } = 0;
    public long VpointPerGame { get; set; } = 1;
    public string DefaultCompanyName { get; set; } = "";
    public string LatestVersion { get; set; } = "02.10.04";
    public string AdminPassword { get; set; } = "";
    public bool LogRequests { get; set; } = true;
    /// <summary>Mall data (catalogue, backgrounds, previews, ads); see tools/import-content.ps1.</summary>
    public string ContentDir { get; set; } = "data/content";
    /// <summary>true: mall items cost 0 V-points. false: the catalogue price is deducted from the member's V-points.</summary>
    public bool MallFree { get; set; } = true;
    /// <summary>TCP port of the online lobby (original online.vdarts.net:8008). 0 disables it.</summary>
    public int LobbyPort { get; set; } = 8008;
}

public sealed class Req
{
    public HttpContext Ctx;
    public string Method, Path, RawBody = "", Ip;
    public Dictionary<string, string> P = new(StringComparer.OrdinalIgnoreCase);
    public IFormFileCollection Files;
    public string this[string k] => P.TryGetValue(k, out var v) ? v : null;

    public static async Task<Req> Read(HttpContext c)
    {
        var r = new Req { Ctx = c, Method = c.Request.Method.ToUpperInvariant(), Path = c.Request.Path.Value ?? "/", Ip = c.Connection.RemoteIpAddress?.MapToIPv4().ToString() };
        Util.ParseForm(c.Request.QueryString.Value, r.P);
        var ct = c.Request.ContentType ?? "";
        if (ct.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            var f = await c.Request.ReadFormAsync();
            foreach (var kv in f) r.P[kv.Key] = kv.Value.ToString();
            r.Files = f.Files;
            r.RawBody = "[multipart " + string.Join(",", f.Keys) + "]";
        }
        else if (c.Request.ContentLength > 0 || c.Request.Headers.TransferEncoding.Count > 0)
        {
            using var sr = new StreamReader(c.Request.Body, Encoding.UTF8);
            r.RawBody = await sr.ReadToEndAsync();
            // the client never sends real JSON bodies except vpointBet "OnlineGambling" — keep raw, still try form parse
            Util.ParseForm(r.RawBody, r.P);
        }
        return r;
    }
}
