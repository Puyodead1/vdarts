using System.Globalization;

namespace VDartsServer;

public record RankEntry(long MemberId, string Nickname, string Nation, string Score, string Dealer, double Value);

/// <summary>
/// Leaderboards computed from uploaded game records. Codes requested by the client (JsonRankings.cs):
/// 202 501, 208 online 501, 301 Cricket, 308 online Cricket, 101 CountUp, 102 online CountUp, 603 Sniper, 604 online Sniper.
/// Score-based games (CountUp/Sniper) rank by best score, 01/Cricket by best rating (PPD / MPR) of a single game.
/// Window: records from the last <see cref="Days"/> days (0 = all time).
/// </summary>
public static class Rankings
{
    public static int Days = 0;

    public static bool ScoreBased(long game) => game is 101 or 102 or 603 or 604 || (game >= 100 && game < 200) || (game >= 600 && game < 700);

    public static List<RankEntry> Board(Db db, long game, int limit, int? days = null)
    {
        bool byScore = ScoreBased(game);
        string col = byScore ? "score" : "rating";
        int d = days ?? Days;
        string since = d > 0 ? DateTime.Now.AddDays(-d).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "0000";
        var rows = db.Query($@"
SELECT r.member_id, m.nickname, m.nation, MAX(r.{col}) AS best,
       (SELECT mc.company_name FROM records r2 LEFT JOIN machines mc ON mc.mac = r2.mac
         WHERE r2.member_id = r.member_id AND r2.game_type = $1 ORDER BY r2.{col} DESC LIMIT 1) AS dealer
FROM records r JOIN members m ON m.id = r.member_id
WHERE r.game_type = $1 AND r.created >= $2 AND m.is_locked = 0
GROUP BY r.member_id
ORDER BY best DESC
LIMIT $3", game, since, limit);
        return rows.Select(x =>
        {
            double v = x["best"] == null ? 0 : Convert.ToDouble(x["best"], CultureInfo.InvariantCulture);
            string s = byScore ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.00", CultureInfo.InvariantCulture);
            return new RankEntry(Util.L(x["member_id"]), Util.S(x["nickname"]), Util.S(x["nation"]), s, Util.S(x["dealer"]), v);
        }).ToList();
    }
}
