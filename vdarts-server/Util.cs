using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VDartsServer;

public static class Util
{
    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>64 lowercase hex chars — the client REQUIRES game_token to be exactly 64 chars.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// Tokens arrive in several shapes: the 64-char "PHP" token, its 128-char hex-of-ASCII "Java" form
    /// (APICore.ToJavaToken / PHP_API.translatePHPtoJava), sometimes wrapped in JSON quotes or brackets.
    /// Normalise everything to the 64-char form.
    /// </summary>
    public static string NormToken(string t)
    {
        if (string.IsNullOrEmpty(t)) return "";
        t = t.Trim().Trim('"', '[', ']', ' ');
        if (t.Length == 128 && t.All(Uri.IsHexDigit))
        {
            try
            {
                var bytes = Convert.FromHexString(t);
                var s = Encoding.ASCII.GetString(bytes);
                if (s.All(c => c >= 0x21 && c < 0x7f)) return s;
            }
            catch { }
        }
        return t;
    }

    public static string ToJavaToken(string php) => Convert.ToHexString(Encoding.ASCII.GetBytes(php));

    /// <summary>
    /// Lenient form parser. APICore builds bodies by string concatenation with NO url-encoding (raw JSON,
    /// quotes, brackets...), WWWForm bodies are properly encoded. We split on '&amp;', take the first '='
    /// and only percent-decode when the value looks encoded.
    /// </summary>
    public static Dictionary<string, string> ParseForm(string s, Dictionary<string, string> into = null)
    {
        var d = into ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(s)) return d;
        if (s.StartsWith("?")) s = s.Substring(1);
        foreach (var part in s.Split('&'))
        {
            if (part.Length == 0) continue;
            int eq = part.IndexOf('=');
            string k = eq < 0 ? part : part.Substring(0, eq);
            string v = eq < 0 ? "" : part.Substring(eq + 1);
            d[Decode(k)] = Decode(v);
        }
        return d;
    }

    private static string Decode(string v)
    {
        // raw JSON payloads ("records=[{...}]") are never encoded; '+' would be a literal there.
        bool looksRaw = v.IndexOfAny(new[] { '{', '[', '"', ' ' }) >= 0;
        if (looksRaw) return v;
        try { return Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { return v; }
    }

    public static string S(object o) => o switch
    {
        null => "",
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""
    };

    public static long L(object o, long def = 0) => o == null ? def : long.TryParse(S(o), out var v) ? v : def;

    public static JsonNode TryJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return JsonNode.Parse(s, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }); } catch { return null; }
    }

    public static readonly JsonSerializerOptions JsonOpts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Json(JsonNode n) => n?.ToJsonString(JsonOpts) ?? "{}";

    // ----------------------------------------------------------------- ratings
    // The client expects "level strings" like "B10" / "AA21.78" / "N1": it takes the first run of digits as an
    // integer level (handicaps treat < 3 as a beginner). The original server's formula is unknown; we derive a
    // level from the PPD (01 games) / MPR (cricket) the client uploads with every record.
    public static int Level01(double ppd) => Math.Clamp((int)Math.Floor(ppd / 3.0), 1, 18);
    public static int LevelCr(double mpr) => Math.Clamp((int)Math.Floor(mpr * 3.3), 1, 18);

    public static string ClassLetter(int level) => level switch
    {
        <= 2 => "N",
        <= 5 => "C",
        <= 8 => "B",
        <= 12 => "A",
        <= 15 => "AA",
        _ => "SA"
    };

    public static string LevelString(int level) => ClassLetter(level) + level.ToString(CultureInfo.InvariantCulture);

    public static bool Is01(long gameType) => gameType >= 200 && gameType < 300;
    public static bool IsCricket(long gameType) => gameType >= 300 && gameType < 400;
}
