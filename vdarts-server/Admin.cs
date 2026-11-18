using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace VDartsServer;

/// <summary>
/// Minimal operator web UI at /admin: members &amp; cards, game keys, machines, announcements, leagues, feedback.
/// Without Vdarts:AdminPassword it only answers requests from localhost; with it, HTTP basic auth (any user name).
/// </summary>
public static class Admin
{
    private static Db _db; private static Members _m; private static ServerOptions _o;

    public static void Map(WebApplication app, Db db, Members m, ServerOptions o) { _db = db; _m = m; _o = o; }

    private static string H(object o) => WebUtility.HtmlEncode(Util.S(o));

    public static bool Authorized(HttpContext c)
    {
        if (string.IsNullOrEmpty(_o.AdminPassword)) return c.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
        var h = c.Request.Headers.Authorization.ToString();
        if (!h.StartsWith("Basic ")) return false;
        try { var s = Encoding.UTF8.GetString(Convert.FromBase64String(h[6..])); return s[(s.IndexOf(':') + 1)..] == _o.AdminPassword; } catch { return false; }
    }

    public static async Task Handle(HttpContext c, Req r)
    {
        if (!Authorized(c))
        {
            c.Response.StatusCode = 401;
            if (!string.IsNullOrEmpty(_o.AdminPassword)) c.Response.Headers.WWWAuthenticate = "Basic realm=\"vdarts-server\"";
            await c.Response.WriteAsync("admin: unauthorized (set Vdarts:AdminPassword to allow remote access)");
            return;
        }
        var path = r.Path.TrimEnd('/').ToLowerInvariant();
        if (r.Method == "POST")
        {
            string err = null;
            try { err = Action(path, r); } catch (Exception e) { err = e.Message; }
            c.Response.Redirect("/admin" + (err == null ? "" : "?err=" + Uri.EscapeDataString(err)));
            return;
        }
        if (path == "/admin/api/members") { await Write(c, "application/json", Util.Json(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(_db.Query("SELECT id,account,nickname,nation,vch,gft,vpoint,is_locked,created,last_login,(SELECT group_concat(card_no) FROM cards WHERE member_id=members.id) cards FROM members"))))); return; }
        await Write(c, "text/html; charset=utf-8", Page(r));
    }

    private static Task Write(HttpContext c, string ct, string body) { c.Response.ContentType = ct; return c.Response.WriteAsync(body); }

    private static string Action(string path, Req r)
    {
        string V(string k) => (r[k] ?? "").Trim();
        switch (path)
        {
            case "/admin/member":
            {
                long id = Util.L(V("id"));
                var card = V("card");
                if (id == 0)
                {
                    if (card != "" && !Members.ValidCard(card)) return "invalid card number";
                    if (card != "" && _m.ByCard(card) != null) return "card already assigned";
                    var acc = V("account");
                    if (acc != "" && _m.ByAccount(acc) != null) return "account exists";
                    var nm = _m.CreateMember(card == "" ? null : card, acc, V("password"), V("nickname"), V("nation"), "");
                    id = Util.L(nm["id"]);
                }
                else if (card != "")
                {
                    if (!Members.ValidCard(card)) return "invalid card number";
                    _db.Exec("INSERT OR REPLACE INTO cards(card_no, member_id, created) VALUES($1,$2,$3)", card, id, Util.Now());
                }
                if (V("nickname") != "") _db.Exec("UPDATE members SET nickname=$1 WHERE id=$2", V("nickname"), id);
                if (V("nation") != "") _db.Exec("UPDATE members SET nation=$1 WHERE id=$2", V("nation").ToUpperInvariant(), id);
                if (V("password") != "") _db.Exec("UPDATE members SET password=$1 WHERE id=$2", V("password"), id);
                if (V("vch") != "") _db.Exec("UPDATE members SET vch=$1 WHERE id=$2", Util.L(V("vch")), id);
                if (V("gft") != "") _db.Exec("UPDATE members SET gft=$1 WHERE id=$2", Util.L(V("gft")), id);
                if (V("vpoint") != "") _db.Exec("UPDATE members SET vpoint=$1 WHERE id=$2", Util.L(V("vpoint")), id);
                if (r["rank01"] != null) _db.Exec("UPDATE members SET rank01_override=NULLIF($1,'') WHERE id=$2", V("rank01"), id);
                if (r["rankcr"] != null) _db.Exec("UPDATE members SET rankcr_override=NULLIF($1,'') WHERE id=$2", V("rankcr"), id);
                if (r["locked"] != null) _db.Exec("UPDATE members SET is_locked=$1 WHERE id=$2", V("locked") == "1" ? 1 : 0, id);
                return null;
            }
            case "/admin/card/delete": _db.Exec("DELETE FROM cards WHERE card_no=$1", V("card")); return null;
            case "/admin/gamekey":
                if (!Members.ValidCard(V("card"))) return "invalid card number";
                _db.Exec("INSERT OR REPLACE INTO game_keys(card_no, mac, note) VALUES($1,$2,$3)", V("card"), V("mac").ToUpperInvariant(), V("note")); return null;
            case "/admin/gamekey/delete": _db.Exec("DELETE FROM game_keys WHERE card_no=$1", V("card")); return null;
            case "/admin/machine":
            {
                var mac = V("mac").ToUpperInvariant(); if (mac == "") return "mac required";
                _db.Exec("INSERT OR IGNORE INTO machines(mac, first_seen) VALUES($1,$2)", mac, Util.Now());
                _db.Exec("UPDATE machines SET name=$1, company_name=$2, dealer_psw=$3, is_locked=$4, lock_after=$5 WHERE mac=$6",
                    V("name"), V("company_name"), V("dealer_psw"), V("is_locked") == "1" ? 1 : 0, V("lock_after") == "" ? "2099-12-31 00:00:00" : V("lock_after"), mac);
                return null;
            }
            case "/admin/machine/delete": _db.Exec("DELETE FROM machines WHERE mac=$1", V("mac")); return null;
            case "/admin/announcement":
                if (V("title") == "") return "title required";
                _db.Exec("INSERT INTO announcements(title, content, update_time) VALUES($1,$2,$3)", V("title"), V("content") == "" ? "-" : V("content"), Util.Now()); return null;
            case "/admin/announcement/delete": _db.Exec("DELETE FROM announcements WHERE id=$1", Util.L(V("id"))); return null;
            case "/admin/league":
            {
                var detail = V("detail") == "" ? "{}" : V("detail");
                if (Util.TryJson(detail) is not JsonObject) return "detail must be a JSON object";
                long id = Util.L(V("id"));
                if (id == 0) _db.Exec("INSERT INTO leagues(mac, season, name, team_name1, team_name2, detail, created) VALUES($1,$2,$3,$4,$5,$6,$7)",
                    V("mac").ToUpperInvariant(), V("season") == "" ? "1" : V("season"), V("name") == "" ? "League" : V("name"), V("team_name1"), V("team_name2"), detail, Util.Now());
                else _db.Exec("UPDATE leagues SET mac=$1, season=$2, name=$3, team_name1=$4, team_name2=$5, detail=$6 WHERE id=$7",
                    V("mac").ToUpperInvariant(), V("season"), V("name"), V("team_name1"), V("team_name2"), detail, id);
                return null;
            }
            case "/admin/league/delete": _db.Exec("DELETE FROM leagues WHERE id=$1", Util.L(V("id"))); return null;
            case "/admin/leaguerank":
                if (Util.TryJson(V("json")) is not JsonObject) return "must be a JSON object like {\"list\":[...]}";
                _db.SetSetting("leagueTeamRank", V("json")); return null;
        }
        return "unknown action";
    }

    private static string Page(Req r)
    {
        var sb = new StringBuilder();
        sb.Append(@"<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width'><title>vdarts-server admin</title>
<style>body{font:14px system-ui,sans-serif;margin:16px;background:#f6f7f9;color:#222}h2{margin-top:28px}table{border-collapse:collapse;background:#fff;width:100%}
td,th{border:1px solid #ddd;padding:4px 6px;text-align:left;vertical-align:top}th{background:#eef}input,textarea{font:inherit}form.inline{display:inline}
.err{background:#fdd;padding:8px;border:1px solid #c66}.box{background:#fff;border:1px solid #ddd;padding:10px;margin:8px 0}small{color:#666}</style>
<h1>vdarts-server</h1>");
        if (r["err"] != null) sb.Append("<p class=err>").Append(H(r["err"])).Append("</p>");

        sb.Append("<h2>Members</h2><table><tr><th>id</th><th>cards</th><th>account</th><th>nickname</th><th>nation</th><th>vch</th><th>gft</th><th>vpoint</th><th>rank 01 / cricket / avg</th><th>games</th><th>locked</th><th>last login</th></tr>");
        foreach (var m in _db.Query("SELECT * FROM members ORDER BY id"))
        {
            var (a, b, c) = _m.Ratings(m);
            var cards = string.Join(" ", _db.Query("SELECT card_no FROM cards WHERE member_id=$1", m["id"]).Select(x =>
                $"{H(x["card_no"])}<form class=inline method=post action=/admin/card/delete><input type=hidden name=card value='{H(x["card_no"])}'><button title=unassign>×</button></form>"));
            sb.Append($"<tr><td>{H(m["id"])}</td><td>{cards}</td><td>{H(m["account"])}</td><td>{H(m["nickname"])}</td><td>{H(m["nation"])}</td><td>{H(m["vch"])}</td><td>{H(m["gft"])}</td><td>{H(m["vpoint"])}</td><td>{H(a)} / {H(b)} / {H(c)}</td><td>{H(_db.Scalar("SELECT COUNT(*) FROM records WHERE member_id=$1", m["id"]))}</td><td>{(Util.L(m["is_locked"]) != 0 ? "yes" : "")}</td><td>{H(m["last_login"])}</td></tr>");
        }
        sb.Append(@"</table><div class=box><b>Add / edit member</b> <small>leave id empty to create; empty fields are left unchanged; rank override like B7 / A10 (empty = computed from games)</small>
<form method=post action=/admin/member>id <input name=id size=5> card <input name=card size=12> account <input name=account size=10> password <input name=password size=8>
nickname <input name=nickname size=12> nation <input name=nation size=3 placeholder=TW> vch <input name=vch size=5> gft <input name=gft size=5> vpoint <input name=vpoint size=5>
rank01 <input name=rank01 size=4> rankCR <input name=rankcr size=4> locked <select name=locked><option value=''>-</option><option value=0>no</option><option value=1>yes</option></select> <button>save</button></form></div>");

        sb.Append("<h2>Game keys (operator cards)</h2><p><small>Built-in staff cards 2513081404 / 1762817609 always work. Empty MAC = valid on every machine.</small></p><table><tr><th>card</th><th>mac</th><th>note</th><th></th></tr>");
        foreach (var k in _db.Query("SELECT * FROM game_keys ORDER BY card_no"))
            sb.Append($"<tr><td>{H(k["card_no"])}</td><td>{H(k["mac"])}</td><td>{H(k["note"])}</td><td><form class=inline method=post action=/admin/gamekey/delete><input type=hidden name=card value='{H(k["card_no"])}'><button>delete</button></form></td></tr>");
        sb.Append("</table><div class=box><form method=post action=/admin/gamekey>card <input name=card size=12> mac <input name=mac size=18> note <input name=note> <button>add</button></form></div>");

        sb.Append("<h2>Machines</h2><table><tr><th>mac</th><th>name</th><th>company</th><th>locked</th><th>lock after</th><th>coins in/used</th><th>prices</th><th>ip</th><th>last seen</th></tr>");
        foreach (var mc in _db.Query("SELECT * FROM machines ORDER BY last_seen DESC"))
            sb.Append($"<tr><td>{H(mc["mac"])}</td><td>{H(mc["name"])}</td><td>{H(mc["company_name"])}</td><td>{(Util.L(mc["is_locked"]) != 0 ? "yes" : "")}</td><td>{H(mc["lock_after"])}</td><td>{H(mc["coin_insert"])}/{H(mc["coin_consume"])}</td><td><small>{H(mc["prices"])}</small></td><td>{H(mc["client_ip"])}</td><td>{H(mc["last_seen"])}</td></tr>");
        sb.Append("</table><div class=box><form method=post action=/admin/machine>mac <input name=mac size=18> name <input name=name> company <input name=company_name> dealer password <input name=dealer_psw size=8> locked <select name=is_locked><option value=0>no</option><option value=1>yes</option></select> lock after <input name=lock_after placeholder='2099-12-31 00:00:00'> <button>save</button></form></div>");

        sb.Append("<h2>Recent games</h2><table><tr><th>time</th><th>game</th><th>member</th><th>rating</th><th>score</th><th>machine</th></tr>");
        foreach (var g in _db.Query("SELECT r.*, m.nickname FROM records r LEFT JOIN members m ON m.id=r.member_id ORDER BY r.id DESC LIMIT 30"))
            sb.Append($"<tr><td>{H(g["created"])}</td><td>{H(g["game_type"])}</td><td>{H(g["nickname"])} ({H(g["member_id"])})</td><td>{H(g["rating"])}</td><td>{H(g["score"])}</td><td>{H(g["mac"])}</td></tr>");
        sb.Append("</table>");

        sb.Append("<h2>Announcements</h2><table><tr><th>id</th><th>title</th><th>content</th><th>time</th><th></th></tr>");
        foreach (var a in _db.Query("SELECT * FROM announcements ORDER BY id DESC"))
            sb.Append($"<tr><td>{H(a["id"])}</td><td>{H(a["title"])}</td><td>{H(a["content"])}</td><td>{H(a["update_time"])}</td><td><form class=inline method=post action=/admin/announcement/delete><input type=hidden name=id value='{H(a["id"])}'><button>delete</button></form></td></tr>");
        sb.Append("</table><div class=box><form method=post action=/admin/announcement>title <input name=title size=30> content <input name=content size=60> <button>add</button></form></div>");

        sb.Append("<h2>Leagues</h2><p><small>Detail is the JSON leagueMatch.php returns (team1/team2/match_detail...; see README). Results posted by cabinets are merged into it.</small></p><table><tr><th>id</th><th>mac</th><th>season</th><th>name</th><th>teams</th><th>detail</th><th></th></tr>");
        foreach (var l in _db.Query("SELECT * FROM leagues ORDER BY id"))
            sb.Append($"<tr><td>{H(l["id"])}</td><td>{H(l["mac"])}</td><td>{H(l["season"])}</td><td>{H(l["name"])}</td><td>{H(l["team_name1"])} vs {H(l["team_name2"])}</td><td><small>{H(Util.S(l["detail"]).Length > 200 ? Util.S(l["detail"])[..200] + "…" : l["detail"])}</small></td><td><form class=inline method=post action=/admin/league/delete><input type=hidden name=id value='{H(l["id"])}'><button>delete</button></form></td></tr>");
        sb.Append("</table><div class=box><form method=post action=/admin/league>id <input name=id size=4 placeholder=new> mac <input name=mac size=18 placeholder='all'> season <input name=season size=3> name <input name=name> team1 <input name=team_name1> team2 <input name=team_name2><br>detail JSON<br><textarea name=detail rows=4 cols=100>{}</textarea><br><button>save</button></form></div>");
        sb.Append($"<div class=box><form method=post action=/admin/leaguerank>leagueTeamRank JSON<br><textarea name=json rows=3 cols=100>{H(_db.Setting("leagueTeamRank", "{\"list\":[]}"))}</textarea><br><button>save</button></form></div>");

        sb.Append("<h2>Feedback</h2><table><tr><th>time</th><th>member</th><th>type</th><th>opinion</th></tr>");
        foreach (var f in _db.Query("SELECT * FROM feedback ORDER BY id DESC LIMIT 30"))
            sb.Append($"<tr><td>{H(f["created"])}</td><td>{H(f["member_id"])}</td><td>{H(f["type"])}</td><td>{H(f["opinion"])}</td></tr>");
        sb.Append("</table>");
        return sb.ToString();
    }
}
