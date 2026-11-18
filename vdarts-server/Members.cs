using System.Text.Json.Nodes;

namespace VDartsServer;

/// <summary>Member / card / token / machine logic shared by the PHP and Java style endpoints.</summary>
public sealed class Members
{
    private readonly Db _db;
    private readonly ServerOptions _o;
    public Members(Db db, ServerOptions o) { _db = db; _o = o; }
    /// <summary>Supplies card_login.fx (chosen backgrounds / custom texts); set to Mall.Used.</summary>
    public Func<long, JsonObject> FxOf;

    public static bool ValidCard(string card) => !string.IsNullOrEmpty(card) && card.Length >= 4 && card.Length <= 32 && card.All(char.IsLetterOrDigit);

    public Dictionary<string, object> ById(long id) => _db.Row("SELECT * FROM members WHERE id=$1", id);

    public Dictionary<string, object> ByCard(string card) =>
        _db.Row("SELECT m.* FROM members m JOIN cards c ON c.member_id=m.id WHERE c.card_no=$1", card);

    public Dictionary<string, object> ByAccount(string account) => _db.Row("SELECT * FROM members WHERE account=$1", account);

    public Dictionary<string, object> ByToken(string token)
    {
        var t = Util.NormToken(token);
        if (t.Length == 0) return null;
        return _db.Row("SELECT m.* FROM members m JOIN tokens t ON t.member_id=m.id WHERE t.token=$1", t);
    }

    /// <summary>Find the member owning a card; optionally auto-create one (AutoRegisterCards).</summary>
    public Dictionary<string, object> ByCardOrRegister(string card)
    {
        if (!ValidCard(card)) return null;
        var m = ByCard(card);
        if (m != null || !_o.AutoRegisterCards) return m;
        return CreateMember(card, null, null, null, "TW", "");
    }

    public Dictionary<string, object> CreateMember(string card, string account, string password, string nickname, string nation, string email)
    {
        var tail = card != null && card.Length >= 4 ? card[^4..] : Random.Shared.Next(1000, 9999).ToString();
        account = string.IsNullOrWhiteSpace(account) ? "card" + (card ?? Util.NewToken()[..10]) : account;
        nickname = string.IsNullOrWhiteSpace(nickname) ? "Player" + tail : nickname;
        var id = _db.Insert("INSERT INTO members(account,password,nickname,nation,email,vch,gft,vpoint,created) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)",
            account, password ?? "", nickname, string.IsNullOrWhiteSpace(nation) ? "TW" : nation, email ?? "",
            _o.DefaultBalance, 0, _o.DefaultVpoint, Util.Now());
        if (card != null) _db.Exec("INSERT INTO cards(card_no, member_id, created) VALUES($1,$2,$3)", card, id, Util.Now());
        return ById(id);
    }

    public string IssueToken(long memberId, string mac)
    {
        var t = Util.NewToken();
        _db.Exec("INSERT INTO tokens(token, member_id, mac, created) VALUES($1,$2,$3,$4)", t, memberId, mac ?? "", Util.Now());
        _db.Exec("UPDATE members SET last_login=$1 WHERE id=$2", Util.Now(), memberId);
        return t;
    }

    public (string r01, string rcr, string avg) Ratings(Dictionary<string, object> m)
    {
        long id = Util.L(m["id"]);
        int? l01 = AvgLevel(id, true), lcr = AvgLevel(id, false);
        string r01 = Util.S(m["rank01_override"]); string rcr = Util.S(m["rankcr_override"]);
        if (r01 == "") r01 = Util.LevelString(l01 ?? 1);
        if (rcr == "") rcr = Util.LevelString(lcr ?? 1);
        var a = (int)Math.Round((Lvl(r01) + Lvl(rcr)) / 2.0, MidpointRounding.AwayFromZero);
        return (r01, rcr, Util.LevelString(Math.Max(1, a)));
    }

    private static int Lvl(string s)
    {
        int n = 0; bool f = false;
        foreach (var ch in s) { if (char.IsDigit(ch)) { n = n * 10 + (ch - '0'); f = true; } else if (f) break; }
        return Math.Max(1, n);
    }

    /// <summary>Average level over the member's last 30 games of that family (null = no games).</summary>
    private int? AvgLevel(long memberId, bool is01)
    {
        var rows = _db.Query(is01
            ? "SELECT rating FROM records WHERE member_id=$1 AND game_type BETWEEN 200 AND 299 AND rating>0 ORDER BY id DESC LIMIT 30"
            : "SELECT rating FROM records WHERE member_id=$1 AND game_type BETWEEN 300 AND 399 AND rating>0 ORDER BY id DESC LIMIT 30", memberId);
        if (rows.Count == 0) return null;
        var avg = rows.Average(r => Convert.ToDouble(r["rating"]));
        return is01 ? Util.Level01(avg) : Util.LevelCr(avg);
    }

    public string LogoUrl(Dictionary<string, object> m, string baseUrl) => baseUrl + "/picture/portrait/" + Util.S(m["id"]) + ".png";

    /// <summary>
    /// Member profile in the shape the client reads from login.php ("card_login"/"member_login"), Java /member and
    /// memberGet.php. Every value is a non-empty string: the client's SimpleJSON treats "" like a missing key and
    /// turns JSON null into the string "null".
    /// </summary>
    public JsonObject Profile(Dictionary<string, object> m, string baseUrl)
    {
        var (r01, rcr, avg) = Ratings(m);
        var vch = Util.L(m["vch"]); var gft = Util.L(m["gft"]);
        var card = Util.S(_db.Scalar("SELECT card_no FROM cards WHERE member_id=$1 ORDER BY created LIMIT 1", m["id"]));
        return new JsonObject
        {
            ["member_id"] = Util.S(m["id"]),
            ["account"] = Util.S(m["account"]),
            ["nickname"] = Util.S(m["nickname"]),
            ["nation"] = Util.S(m["nation"]),
            ["logo"] = LogoUrl(m, baseUrl),
            ["email"] = Util.S(m["email"]) == "" ? "-" : Util.S(m["email"]),
            ["address"] = "-",
            ["phone"] = "000",
            ["card_no"] = card == "" ? "-" : card,
            ["is_locked"] = Util.L(m["is_locked"]) != 0 ? "true" : "false",
            ["vpoint"] = Util.S(Util.L(m["vpoint"])),
            ["vcoin"] = Util.S(vch + gft),
            ["consume_rank"] = "1",
            ["rank"] = new JsonObject { ["2"] = r01, ["3"] = rcr, ["average"] = avg, ["league"] = new JsonObject { ["average"] = avg } },
            ["balance"] = new JsonObject { ["pay.vch"] = Util.S(vch), ["pay.gft"] = Util.S(gft) },
            ["fx"] = FxOf?.Invoke(Util.L(m["id"])) ?? new JsonObject { ["txt"] = new JsonObject(), ["bak"] = new JsonObject(), ["dbg"] = new JsonObject(), ["mbg"] = new JsonObject() },
        };
    }

    // ------------------------------------------------------------------ machines
    public Dictionary<string, object> Machine(string mac, string clientIp = null, bool touch = true)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        mac = mac.Trim().ToUpperInvariant();
        var row = _db.Row("SELECT * FROM machines WHERE mac=$1", mac);
        if (row == null)
        {
            if (!_o.AutoRegisterMachines) return null;
            _db.Exec("INSERT OR IGNORE INTO machines(mac, name, company_name, first_seen, last_seen, client_ip) VALUES($1,$2,$3,$4,$4,$5)",
                mac, "VDarts-" + mac.Replace("-", "")[^Math.Min(6, mac.Replace("-", "").Length)..], _o.DefaultCompanyName, Util.Now(), clientIp ?? "");
            row = _db.Row("SELECT * FROM machines WHERE mac=$1", mac);
        }
        else if (touch)
        {
            _db.Exec("UPDATE machines SET last_seen=$1, client_ip=COALESCE(NULLIF($2,''), client_ip) WHERE mac=$3", Util.Now(), clientIp ?? "", mac);
        }
        return row;
    }

    public bool IsGameKey(string card, string mac)
    {
        if (string.IsNullOrEmpty(card)) return false;
        if (card == "2513081404" || card == "1762817609") return true; // hard-coded VDarts staff cards (GameKey.cs)
        var row = _db.Row("SELECT * FROM game_keys WHERE card_no=$1", card);
        if (row == null) return false;
        var km = Util.S(row["mac"]);
        return km == "" || string.Equals(km, mac?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
