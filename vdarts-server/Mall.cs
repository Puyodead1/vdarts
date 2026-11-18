using System.Globalization;
using System.Text.Json.Nodes;

namespace VDartsServer;

/// <summary>
/// Effects mall (hit-effect videos, sounds, backgrounds, custom texts) and idle-screen ads, served from local data.
///
/// Content directory (Vdarts:ContentDir, default data/content), filled by tools/import-content.ps1:
///   fx/fxPersonal_all_&lt;lang&gt;.json   item catalogue (effects + sounds), as archived from the official server
///   fx/fxGetAllBak.json               background index {"fx_.bak.001":"&lt;uuid&gt;", ...}
///   fx/bak/&lt;uuid&gt;.jpg               background images (in-game download + mall preview)
///   fx/video/&lt;url&gt;.jpg              effect preview images (optional)
///   ads/pic/*, ads/video/*.ogv        idle-loop ads
///
/// The effect videos / sounds themselves ship with the game (StreamingAssets/Effect/fx_.vid.NNN.ogv,
/// resources Audio/fx_.wav.NNN), so the server only has to track who owns and uses what.
/// Purchases are persisted per member; Vdarts:MallFree=true (default) makes everything cost 0 V-points.
/// </summary>
public sealed class Mall
{
    private readonly Db _db;
    private readonly ServerOptions _o;
    private readonly string _dir;
    private readonly Dictionary<string, JsonArray> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private JsonObject _bakIndex;
    private readonly object _lock = new();
    public static readonly string[] Targets = { "gam.01g", "gam.crk", "gam.cup" };
    public const string Forever = "2099-12-31 23:59:59";

    public Mall(Db db, ServerOptions o)
    {
        _db = db; _o = o;
        _dir = Path.GetFullPath(string.IsNullOrEmpty(o.ContentDir) ? "data/content" : o.ContentDir);
        _db.Exec(@"
CREATE TABLE IF NOT EXISTS fx_owned(member_id INTEGER NOT NULL, fx TEXT NOT NULL, type TEXT NOT NULL DEFAULT '', target TEXT NOT NULL DEFAULT '',
  expiry_date TEXT NOT NULL, created TEXT NOT NULL, PRIMARY KEY(member_id, fx));
CREATE TABLE IF NOT EXISTS fx_used(member_id INTEGER NOT NULL, slot TEXT NOT NULL, target TEXT NOT NULL, fx TEXT NOT NULL,
  PRIMARY KEY(member_id, slot, target));
CREATE TABLE IF NOT EXISTS fx_bg_used(member_id INTEGER NOT NULL, target TEXT NOT NULL, kind TEXT NOT NULL, fx TEXT NOT NULL, url TEXT NOT NULL,
  PRIMARY KEY(member_id, target));
CREATE TABLE IF NOT EXISTS fx_text(member_id INTEGER NOT NULL, type TEXT NOT NULL, text TEXT NOT NULL, PRIMARY KEY(member_id, type));");
    }

    public string ContentDir => _dir;

    // ------------------------------------------------------------------ catalogue
    public JsonArray Catalog(string lang)
    {
        lang = (lang ?? "").Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (_catalog.TryGetValue(lang, out var c)) return c;
            var dir = Path.Combine(_dir, "fx");
            string file = null;
            foreach (var l in new[] { lang, "en-us", "zh-tw" })
                if (l != "" && File.Exists(Path.Combine(dir, $"fxPersonal_all_{l}.json"))) { file = Path.Combine(dir, $"fxPersonal_all_{l}.json"); break; }
            file ??= Directory.Exists(dir) ? Directory.GetFiles(dir, "fxPersonal_all_*.json").FirstOrDefault() : null;
            var arr = file == null ? new JsonArray() : (Util.TryJson(File.ReadAllText(file)) as JsonArray ?? new JsonArray());
            _catalog[lang] = arr;
            return arr;
        }
    }

    public JsonObject Item(string key) => Catalog("en-us").OfType<JsonObject>().FirstOrDefault(x => Util.S(x["i18n_index"]?.ToString()) == key);

    public JsonObject BakIndex()
    {
        lock (_lock)
        {
            if (_bakIndex != null) return _bakIndex;
            var f = Path.Combine(_dir, "fx", "fxGetAllBak.json");
            _bakIndex = File.Exists(f) ? (Util.TryJson(File.ReadAllText(f)) as JsonObject ?? new JsonObject()) : new JsonObject();
            return _bakIndex;
        }
    }

    /// <summary>Background keys whose image is actually present (so the client never waits on a 404).</summary>
    public IEnumerable<KeyValuePair<string, string>> Backgrounds() =>
        BakIndex().Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value?.ToString() ?? ""))
                  .Where(kv => kv.Value != "" && File.Exists(Path.Combine(_dir, "fx", "bak", kv.Value + ".jpg")))
                  .OrderBy(kv => kv.Key, StringComparer.Ordinal);

    public static JsonArray DefaultPrices() => new()
    {
        new JsonObject { ["time"] = 0, ["price"] = 600 },
        new JsonObject { ["time"] = 7, ["price"] = 300 },
        new JsonObject { ["time"] = 30, ["price"] = 450 },
    };

    // ------------------------------------------------------------------ helpers
    public static string NormTarget(string g)
    {
        g = (g ?? "").Trim().ToLowerInvariant().Replace('_', '.');
        return Targets.Contains(g) ? g : null;
    }

    static string Today() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    static bool Expired(string exp) =>
        !DateTime.TryParse(exp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d < DateTime.Now;

    string OwnedExpiry(long mid, string fx) => _db.Scalar("SELECT expiry_date FROM fx_owned WHERE member_id=$1 AND fx=$2", mid, fx) as string;

    /// <summary>Charge (unless MallFree) and extend ownership. Returns (expiry, errorInfo).</summary>
    (string, string) Buy(Dictionary<string, object> m, string fx, string type, string target, JsonArray prices, string timeVal)
    {
        if (!int.TryParse(timeVal, out var days) || days < 0) return (null, "time_val error");
        long price = -1;
        foreach (var p in prices.OfType<JsonObject>())
            if (Util.L(p["time"]?.ToString()) == days) price = Util.L(p["price"]?.ToString());
        if (price < 0) return (null, "time_val error");
        if (_o.MallFree) price = 0;
        long mid = Util.L(m["id"]);
        lock (_lock)
        {
            long vp = Util.L(_db.Scalar("SELECT vpoint FROM members WHERE id=$1", mid));
            if (vp < price) return (null, "member vpoint insufficient");
            string cur = OwnedExpiry(mid, fx);
            string exp;
            if (days == 0) exp = Forever;
            else
            {
                var from = DateTime.Now;
                if (cur != null && DateTime.TryParse(cur, CultureInfo.InvariantCulture, DateTimeStyles.None, out var c) && c > from) from = c;
                exp = from.AddDays(days).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                if (cur == Forever) exp = Forever;
            }
            if (price > 0) _db.Exec("UPDATE members SET vpoint=vpoint-$1 WHERE id=$2", price, mid);
            _db.Exec(@"INSERT INTO fx_owned(member_id, fx, type, target, expiry_date, created) VALUES($1,$2,$3,$4,$5,$6)
                       ON CONFLICT(member_id, fx) DO UPDATE SET expiry_date=excluded.expiry_date, type=excluded.type, target=excluded.target",
                mid, fx, type, target ?? "", exp, Today());
            return (exp, null);
        }
    }

    // ------------------------------------------------------------------ per-member views
    /// <summary>fxPersonal.php?token= / member_id= : {"personal":[...],"used":[...],"list":{}}</summary>
    public JsonObject Personal(long mid)
    {
        var personal = new JsonArray();
        foreach (var r in _db.Query("SELECT fx, type, target, expiry_date FROM fx_owned WHERE member_id=$1 AND fx NOT LIKE 'fx_.bak%' AND fx NOT LIKE 'fx_.dbg%' AND fx NOT LIKE 'fx_.mbg%' ORDER BY fx", mid))
        {
            if (Expired(Util.S(r["expiry_date"]))) continue;
            personal.Add(new JsonObject { ["fx"] = Util.S(r["fx"]), ["type"] = Util.S(r["type"]), ["expiry_date"] = Util.S(r["expiry_date"]), ["target"] = Util.S(r["target"]) });
        }
        var used = new JsonArray();
        foreach (var r in _db.Query("SELECT u.slot, u.target, u.fx, o.expiry_date FROM fx_used u LEFT JOIN fx_owned o ON o.member_id=u.member_id AND o.fx=u.fx WHERE u.member_id=$1 ORDER BY u.slot, u.target", mid))
        {
            if (Expired(Util.S(r["expiry_date"]))) continue;
            var it = Item(Util.S(r["fx"]));
            var add = it?["addtion"]?.ToString();
            used.Add(new JsonObject { ["fx"] = Util.S(r["fx"]), ["type"] = Util.S(r["slot"]), ["target"] = Util.S(r["target"]), ["addtion"] = string.IsNullOrEmpty(add) ? "empty" : add });
        }
        return new JsonObject { ["personal"] = personal, ["used"] = used, ["list"] = new JsonObject() };
    }

    /// <summary>fxGetUsed.php and card_login.fx : {"txt":{type:text},"bak":{target:{url}},"dbg":{},"mbg":{}}</summary>
    public JsonObject Used(long mid)
    {
        var o = new JsonObject { ["txt"] = new JsonObject(), ["bak"] = new JsonObject(), ["dbg"] = new JsonObject(), ["mbg"] = new JsonObject() };
        if (mid <= 0) return o;
        foreach (var r in _db.Query("SELECT type, text FROM fx_text WHERE member_id=$1", mid))
            if (Util.S(r["text"]) != "") o["txt"]![Util.S(r["type"])] = Util.S(r["text"]);
        foreach (var r in _db.Query("SELECT b.target, b.kind, b.url, o.expiry_date FROM fx_bg_used b LEFT JOIN fx_owned o ON o.member_id=b.member_id AND o.fx=b.fx WHERE b.member_id=$1", mid))
        {
            if (Expired(Util.S(r["expiry_date"]))) continue;
            var kind = Util.S(r["kind"]);
            if (o[kind] is JsonObject k) k[Util.S(r["target"])] = new JsonObject { ["url"] = Util.S(r["url"]) };
        }
        return o;
    }

    /// <summary>fxGetAllBak2.php : {"list":{key:{name,url,version,expiry_date,price_array}}}</summary>
    public JsonObject AllBackgrounds(long mid)
    {
        var owned = mid > 0
            ? _db.Query("SELECT fx, expiry_date FROM fx_owned WHERE member_id=$1", mid).ToDictionary(r => Util.S(r["fx"]), r => Util.S(r["expiry_date"]))
            : new Dictionary<string, string>();
        var list = new JsonObject();
        foreach (var (key, url) in Backgrounds())
        {
            var e = new JsonObject
            {
                ["name"] = BackgroundName(key),
                ["url"] = url,
                ["version"] = "01.49.00",
                ["price_array"] = DefaultPrices(),
            };
            if (owned.TryGetValue(key, out var exp) && !Expired(exp)) e["expiry_date"] = exp;
            list[key] = e;
        }
        return new JsonObject { ["list"] = list };
    }

    static string BackgroundName(string key)
    {
        var num = key.Length > 8 ? key[8..] : key;
        return (key.StartsWith("fx_.mbg") ? "Member background " : "Background ") + num;
    }

    // ------------------------------------------------------------------ actions
    public JsonObject PurchaseEffect(Dictionary<string, object> m, Req r)
    {
        var key = r["i18n_index"] ?? "";
        var it = Item(key);
        if (it == null) return new JsonObject { ["status"] = "0", ["info"] = "item null" };
        var prices = it["price_array"] as JsonArray ?? DefaultPrices();
        var (exp, err) = Buy(m, key, Util.S(it["type"]?.ToString()), Util.S(it["target"]?.ToString()), prices, r["time_val"]);
        return err != null ? new JsonObject { ["status"] = "0", ["info"] = err } : new JsonObject { ["status"] = "1", ["expiry_date"] = exp };
    }

    public JsonObject UseEffect(Dictionary<string, object> m, Req r)
    {
        long mid = Util.L(m["id"]);
        var key = r["i18n_index"] ?? "";
        var it = Item(key);
        if (it == null) return new JsonObject { ["status"] = "0", ["info"] = "no purchase" };
        var exp = OwnedExpiry(mid, key);
        if (exp == null) return new JsonObject { ["status"] = "0", ["info"] = "no purchase" };
        if (Expired(exp)) return new JsonObject { ["status"] = "0", ["info"] = "passed its expiry_date" };
        var target = NormTarget(r["gam_val"]);
        if (target == null) return new JsonObject { ["status"] = "0", ["info"] = "gam_val error" };
        var itemTarget = Util.S(it["target"]?.ToString());
        if (itemTarget == "gam.crk" && target != "gam.crk") return new JsonObject { ["status"] = "0", ["info"] = "can only be used gam_crk" };
        var type = Util.S(it["type"]?.ToString());
        string slot = type;
        if (type.StartsWith("fx_.wav"))
        {
            slot = r["used_type"] ?? "";
            if (slot == "") return new JsonObject { ["status"] = "0", ["info"] = "used_type null" };
            if (!slot.StartsWith("fx_.wav.")) return new JsonObject { ["status"] = "0", ["info"] = "wav type error" };
        }
        // Using the item that is already in this slot switches it off again (toggle).
        var cur = _db.Scalar("SELECT fx FROM fx_used WHERE member_id=$1 AND slot=$2 AND target=$3", mid, slot, target) as string;
        if (cur == key) _db.Exec("DELETE FROM fx_used WHERE member_id=$1 AND slot=$2 AND target=$3", mid, slot, target);
        else _db.Exec("INSERT INTO fx_used(member_id, slot, target, fx) VALUES($1,$2,$3,$4) ON CONFLICT(member_id, slot, target) DO UPDATE SET fx=excluded.fx", mid, slot, target, key);
        return new JsonObject { ["status"] = "1" };
    }

    public JsonObject PurchaseBackground(Dictionary<string, object> m, Req r)
    {
        var key = r["img_index"] ?? "";
        if (!BakIndex().ContainsKey(key)) return new JsonObject { ["status"] = "0", ["info"] = "item null" };
        var (exp, err) = Buy(m, key, key.Length >= 7 ? key[..7] : key, "", DefaultPrices(), r["time_val"]);
        return err != null ? new JsonObject { ["status"] = "0", ["info"] = err } : new JsonObject { ["status"] = "1", ["expiry_date"] = exp };
    }

    public JsonObject UseBackground(Dictionary<string, object> m, Req r)
    {
        long mid = Util.L(m["id"]);
        var key = r["img_index"] ?? "";
        var url = BakIndex()[key]?.ToString();
        var exp = OwnedExpiry(mid, key);
        if (string.IsNullOrEmpty(url) || exp == null) return new JsonObject { ["status"] = "0", ["info"] = "img is null" };
        if (Expired(exp)) return new JsonObject { ["status"] = "0", ["info"] = "img time expired" };
        var target = NormTarget(r["gam_val"]);
        if (target == null) return new JsonObject { ["status"] = "0", ["info"] = "img is null" };
        var kind = key.StartsWith("fx_.dbg") ? "dbg" : key.StartsWith("fx_.mbg") ? "mbg" : "bak";
        _db.Exec("INSERT INTO fx_bg_used(member_id, target, kind, fx, url) VALUES($1,$2,$3,$4,$5) ON CONFLICT(member_id, target) DO UPDATE SET kind=excluded.kind, fx=excluded.fx, url=excluded.url",
            mid, target, kind, key, url);
        return new JsonObject { ["status"] = "1" };
    }

    public JsonObject RemoveBackground(Dictionary<string, object> m, Req r)
    {
        var target = NormTarget(r["gam_val"]);
        if (target != null) _db.Exec("DELETE FROM fx_bg_used WHERE member_id=$1 AND target=$2", Util.L(m["id"]), target);
        return new JsonObject { ["status"] = "1" };
    }

    public JsonObject SetText(Dictionary<string, object> m, Req r, bool delete)
    {
        var type = r["type"] ?? "";
        if (!type.StartsWith("fx_.txt.")) return new JsonObject { ["status"] = "0", ["info"] = "type error" };
        var text = (r["text"] ?? "").Trim();
        if (text.Length > 40) text = text[..40];
        if (delete || text == "") _db.Exec("DELETE FROM fx_text WHERE member_id=$1 AND type=$2", Util.L(m["id"]), type);
        else _db.Exec("INSERT INTO fx_text(member_id, type, text) VALUES($1,$2,$3) ON CONFLICT(member_id, type) DO UPDATE SET text=excluded.text", Util.L(m["id"]), type, text);
        return new JsonObject { ["status"] = "1" };
    }

    // ------------------------------------------------------------------ files
    /// <summary>Resolve a file under the content dir; null if missing or outside it.</summary>
    public string File_(string relDir, string name)
    {
        name = Uri.UnescapeDataString(name ?? "");
        if (name == "" || name.Contains("..") || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
        var p = Path.GetFullPath(Path.Combine(_dir, relDir, name));
        if (!p.StartsWith(_dir, StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(p)) return p;
        // the mall asks for background previews as .png but the archive has .jpg
        var alt = Path.ChangeExtension(p, ".jpg");
        return File.Exists(alt) ? alt : null;
    }

    public static string MimeOf(string path, byte[] head)
    {
        if (head.Length > 3 && head[0] == 0xFF && head[1] == 0xD8) return "image/jpeg";
        if (head.Length > 3 && head[0] == 0x89 && head[1] == 0x50) return "image/png";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ogv" => "video/ogg",
            ".mp4" => "video/mp4",
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/octet-stream",
        };
    }

    /// <summary>advertisementGet.php : [{"url":".../ads/pic/x.jpg"}, ...] (url MUST contain "/ads/").</summary>
    public JsonArray Ads(string baseUrl)
    {
        var arr = new JsonArray();
        foreach (var sub in new[] { "pic", "video" })
        {
            var d = Path.Combine(_dir, "ads", sub);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.GetFiles(d).OrderBy(x => x, StringComparer.Ordinal))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                bool ok = sub == "video" ? ext == ".ogv" : ext is ".jpg" or ".jpeg" or ".png";
                if (ok) arr.Add(new JsonObject { ["url"] = $"{baseUrl}/ads/{sub}/{Path.GetFileName(f)}" });
            }
        }
        return arr;
    }
}
