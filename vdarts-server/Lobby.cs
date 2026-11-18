using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace VDartsServer;

/// <summary>
/// Online lobby ("remote PK"): replacement for online.vdarts.net:8008.
/// Plain TCP, one JSON object per line in both directions. Rebuilt from the client code (Online.cs /
/// onlineMenuSystem.cs); see docs/lobby-protocol.md for the full message catalogue.
///
/// Key rules the client imposes:
///  * every client message except "exit" must be ACKed with {"type":"repeat","content":{"time":T}}, or the
///    client's stop-and-wait writer stalls for ~100 s;
///  * server→client fields are at the TOP level (invite, hit, talk, remove...), numeric fields must parse;
///  * never send "list" to a player in a game (it discards queued remote darts);
///  * the client never notices a closed socket, so the server must tell the opponent with "game_over".
/// </summary>
public sealed class Lobby
{
    private readonly Members _members;
    private readonly ServerOptions _o;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new();   // lobby id -> session (logged in or not)
    private readonly List<Invite> _invites = new();
    private int _nextId = 100;

    public static readonly TimeSpan InviteTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan GameIdleTimeout = TimeSpan.FromSeconds(75);

    public Lobby(Members members, ServerOptions o, ILogger log) { _members = members; _o = o; _log = log; }

    private sealed class Session
    {
        public string Id;
        public TcpClient Tcp;
        public string Remote;
        public readonly Channel<string> Out = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        public readonly HashSet<string> SeenTimes = new();
        public bool LoggedIn, Closed;
        public long MemberId;
        public string Nickname = "", Nation = "TW", Auto = "false", Version = "02.10", LeagueId = "0", Avg = "1";
        public string SteelBoard = "0", HomeDarts = "0", Gambling = "T";
        public Session Opponent;              // non-null while in an online game
        public DateTime LastGameActivity;
        public bool InGame => Opponent != null;
    }

    private sealed class Invite
    {
        public Session From, To;
        public string GameType;
        public DateTime Deadline;
    }

    // ------------------------------------------------------------------ listener
    public void Start(CancellationToken stop)
    {
        var l = new TcpListener(IPAddress.Any, _o.LobbyPort);
        l.Start();
        _log.LogInformation("lobby listening on tcp/{port}", _o.LobbyPort);
        stop.Register(() => { try { l.Stop(); } catch { } });
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(stop); } catch { break; }
                _ = Task.Run(() => Serve(c));
            }
        });
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested) { await Task.Delay(1000); try { Tick(); } catch (Exception e) { _log.LogError(e, "lobby tick"); } }
        });
    }

    private async Task Serve(TcpClient c)
    {
        c.NoDelay = true;
        var s = new Session { Tcp = c, Remote = c.Client.RemoteEndPoint?.ToString() ?? "?" };
        lock (_gate) { s.Id = (++_nextId).ToString(); _sessions[s.Id] = s; }
        _log.LogInformation("lobby #{id} connect {ep}", s.Id, s.Remote);
        var stream = c.GetStream();
        var writer = Task.Run(() => WriteLoop(s, stream));
        try
        {
            using var rd = new StreamReader(stream, new UTF8Encoding(false));
            while (true)
            {
                var line = await rd.ReadLineAsync();
                if (line == null) break;
                line = line.Trim('\r', '\n', ' ', '\t', '﻿');
                if (line.Length == 0) continue;
                if (!Handle(s, line)) break;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        catch (Exception e) { _log.LogError(e, "lobby #{id}", s.Id); }
        Drop(s, "disconnect");
        try { await writer; } catch { }
        try { c.Close(); } catch { }
    }

    /// <summary>Single writer per connection; an empty line every second is the heartbeat (keeps the client's
    /// connection-quality icon full: it wants a line at least every 1.5 s).</summary>
    private async Task WriteLoop(Session s, NetworkStream stream)
    {
        var w = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
        try
        {
            while (true)
            {
                using var cts = new CancellationTokenSource(1000);
                string msg = null;
                try
                {
                    if (!await s.Out.Reader.WaitToReadAsync(cts.Token)) break;
                    s.Out.Reader.TryRead(out msg);
                }
                catch (OperationCanceledException) { msg = ""; }
                await w.WriteLineAsync(msg ?? "");
                while (s.Out.Reader.TryRead(out var more)) await w.WriteLineAsync(more);
                await w.FlushAsync();
            }
        }
        catch { }
        try { s.Tcp.Close(); } catch { }
    }

    private void Send(Session s, JsonObject o)
    {
        if (s == null || s.Closed) return;
        var line = o.ToJsonString();
        var t = (string)o["type"];
        if (t != "repeat") _log.LogInformation("lobby #{id} <= {m}", s.Id, line);
        s.Out.Writer.TryWrite(line);
    }

    private static JsonObject Msg(string type) => new() { ["type"] = type };

    // ------------------------------------------------------------------ inbound
    /// <returns>false to close the connection.</returns>
    private bool Handle(Session s, string line)
    {
        JsonObject m;
        try { m = JsonNode.Parse(line) as JsonObject; } catch { m = null; }
        if (m == null) { _log.LogWarning("lobby #{id} bad line {l}", s.Id, Trunc(line)); return true; }
        var type = Str(m["type"]);
        var time = Str(m["time"]);
        // log everything but the login token
        _log.LogInformation("lobby #{id} => {m}", s.Id, type == "login" ? $"login account={Str(m["content"]?["account"])} time={time}" : Trunc(line));

        if (type == "exit") return false;

        lock (_gate)
        {
            if (time != "")
            {
                Send(s, new JsonObject { ["type"] = "repeat", ["content"] = new JsonObject { ["time"] = time } });
                if (!s.SeenTimes.Add(time)) return true;   // resend of an already processed message
            }
            if (type != "login" && !s.LoggedIn) { _log.LogWarning("lobby #{id} {t} before login", s.Id, type); return true; }
            switch (type)
            {
                case "login": Login(s, m["content"] as JsonObject); break;
                case "invite": OnInvite(s, m["content"] as JsonObject); break;
                case "cancel_invite": OnCancelInvite(s, Str(m["content"]?["id"])); break;
                case "response": OnResponse(s, Str(m["content"]?["id"]), Str(m["content"]?["response"])); break;
                case "hit":
                    if (s.InGame) { Touch(s); Send(s.Opponent, new JsonObject { ["type"] = "hit", ["area"] = Num(Str(m["area"])) }); }
                    break;
                case "round_end":
                case "round_start":
                    if (s.InGame) { Touch(s); Send(s.Opponent, Msg(type)); }
                    break;
                case "talk":
                    if (s.InGame) Send(s.Opponent, new JsonObject { ["type"] = "talk", ["number"] = Num(Str(m["number"])) });
                    break;
                case "game_over": EndGame(s, "game_over"); break;
                default: _log.LogWarning("lobby #{id} unknown type {t}", s.Id, type); break;
            }
        }
        return true;
    }

    private void Login(Session s, JsonObject c)
    {
        var token = Str(c?["token"]);
        var member = token == "" ? null : _members.ByToken(token);
        if (member == null) { Send(s, Msg(token == "" ? "login_without_token" : "login_fail")); return; }
        if (s.LoggedIn) return;
        long mid = Util.L(member["id"]);
        // A client never notices a dead socket and reconnects with a new one: drop any older session of this member.
        foreach (var old in _sessions.Values.Where(x => x != s && x.LoggedIn && x.MemberId == mid).ToList())
        {
            _log.LogInformation("lobby #{old} replaced by #{id} (member {mid})", old.Id, s.Id, mid);
            DropLocked(old, "replaced");
            try { old.Tcp.Close(); } catch { }
        }
        s.MemberId = mid;
        s.Nickname = Def(Util.S(member["nickname"]), "Player" + mid);
        s.Nation = Def(Util.S(member["nation"]), "TW");
        s.Avg = Def(Util.S(_members.Ratings(member).avg), "1");
        s.Auto = Def(Str(c["auto"]), "false");
        s.Version = Def(Str(c["version"]), "02.10");
        s.LeagueId = Def(Str(c["league_id"]), "0");
        s.SteelBoard = Def(Str(c["data"]?["isSteelBoard"]), "0");
        s.HomeDarts = Def(Str(c["data"]?["isHomeDarts"]), "0");
        s.Gambling = Def(Str(c["data"]?["Gambling"]), "T");
        s.LoggedIn = true;
        Send(s, Msg("login_success"));
        var arr = new JsonArray();
        foreach (var o in Others(s)) arr.Add(PlayerJson(o));
        var list = Msg("list"); list["players"] = arr;
        Send(s, list);
        foreach (var o in Others(s)) Send(o, new JsonObject { ["type"] = "add", ["player"] = PlayerJson(s) });
        _log.LogInformation("lobby #{id} login member {mid} '{n}' ({cnt} online)", s.Id, mid, s.Nickname, _sessions.Values.Count(x => x.LoggedIn));
    }

    private void OnInvite(Session s, JsonObject c)
    {
        var targetId = Str(c?["id"]);
        _sessions.TryGetValue(targetId, out var t);
        // An invite always means the sender is back in the lobby (a rematch sends game_over first; be lenient).
        if (s.InGame) EndGame(s, "invite while in game");
        CancelOutgoing(s);
        if (t == null || !t.LoggedIn || t == s || t.InGame || _invites.Any(i => i.To == t || i.From == t))
        {
            _log.LogInformation("lobby #{id} invite to {t} refused ({why})", s.Id, targetId, t == null || !t.LoggedIn ? "unknown" : t == s ? "self" : "busy");
            Send(s, Msg("refuse"));
            return;
        }
        var gameType = Num(Str(c["game_type"]));
        _invites.Add(new Invite { From = s, To = t, GameType = gameType, Deadline = DateTime.UtcNow + InviteTimeout });
        // The client reads every invite field at the top level, and int.Parse()s match_id/isSteelBoard/isHomeDarts
        // unconditionally, so plain lobby invites (which do not carry them) get "0" defaults.
        Send(t, new JsonObject
        {
            ["type"] = "invite",
            ["id"] = s.Id,
            ["game_type"] = gameType,
            ["game_setting"] = (c["game_setting"] as JsonObject)?.DeepClone() ?? new JsonObject(),
            ["vpoint_bet"] = Num(Str(c["vpoint_bet"])),
            ["match_id"] = Num(Str(c["match_id"])),
            ["isSteelBoard"] = Num(Str(c["isSteelBoard"])),
            ["isHomeDarts"] = Num(Str(c["isHomeDarts"])),
        });
    }

    private void OnCancelInvite(Session s, string targetId)
    {
        var inv = _invites.FirstOrDefault(i => i.From == s && i.To.Id == targetId);
        if (inv == null) return;
        _invites.Remove(inv);
        Send(inv.To, new JsonObject { ["type"] = "cancel_invite", ["id"] = s.Id });
    }

    private void OnResponse(Session s, string inviterId, string response)
    {
        var inv = _invites.FirstOrDefault(i => i.To == s && i.From.Id == inviterId);
        bool yes = response.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (inv == null)
        {
            // The invitee starts its game immediately on "yes"; if the invite is gone (cancelled / timed out /
            // inviter left) tell it the opponent left, so it plays on against the robot instead of hanging.
            if (yes) { _log.LogInformation("lobby #{id} accepted a stale invite from {f}", s.Id, inviterId); Send(s, Msg("game_over")); }
            return;
        }
        _invites.Remove(inv);
        if (!yes) { Send(inv.From, Msg("refuse")); return; }
        var a = inv.From; var b = s;
        _invites.RemoveAll(i => i.From == a || i.To == a || i.From == b || i.To == b);
        a.Opponent = b; b.Opponent = a;
        a.LastGameActivity = b.LastGameActivity = DateTime.UtcNow;
        Send(a, Msg("game_start"));
        _log.LogInformation("lobby game start #{a} vs #{b} type {t}", a.Id, b.Id, inv.GameType);
        var init = new JsonObject { ["type"] = "game_init", ["players"] = new JsonArray(new JsonObject { ["id"] = a.Id }, new JsonObject { ["id"] = b.Id }) };
        foreach (var o in _sessions.Values.Where(x => x.LoggedIn && x != a && x != b)) Send(o, (JsonObject)init.DeepClone());
    }

    // ------------------------------------------------------------------ game / session teardown
    private void Touch(Session s) { s.LastGameActivity = DateTime.UtcNow; if (s.Opponent != null) s.Opponent.LastGameActivity = s.LastGameActivity; }

    /// <summary>Ends s's game (if any): the opponent gets "game_over" (its client then finishes against a robot, or
    /// wins a staked game; ignored if it is already on the result screen) and both are shown idle again.</summary>
    private void EndGame(Session s, string why)
    {
        var o = s.Opponent;
        if (o == null) return;
        s.Opponent = null; o.Opponent = null;
        _log.LogInformation("lobby game end #{a} vs #{b} ({why})", s.Id, o.Id, why);
        Send(o, Msg("game_over"));
        foreach (var p in new[] { s, o })
            if (!p.Closed)
                foreach (var x in _sessions.Values.Where(x => x.LoggedIn && x != p)) Send(x, new JsonObject { ["type"] = "add", ["player"] = PlayerJson(p) });
    }

    private void CancelOutgoing(Session s)
    {
        foreach (var i in _invites.Where(i => i.From == s).ToList())
        {
            _invites.Remove(i);
            Send(i.To, new JsonObject { ["type"] = "cancel_invite", ["id"] = s.Id });
        }
    }

    private void Drop(Session s, string why) { lock (_gate) DropLocked(s, why); }

    private void DropLocked(Session s, string why)
    {
        if (s.Closed) return;
        _log.LogInformation("lobby #{id} {why}", s.Id, why);
        s.Closed = true;   // first: nothing more is sent to s, and EndGame does not re-announce it
        EndGame(s, why);
        CancelOutgoing(s);
        foreach (var i in _invites.Where(i => i.To == s).ToList()) { _invites.Remove(i); Send(i.From, Msg("refuse")); }
        s.Out.Writer.TryComplete();
        _sessions.Remove(s.Id);
        if (s.LoggedIn)
            foreach (var o in _sessions.Values.Where(x => x.LoggedIn)) Send(o, new JsonObject { ["type"] = "remove", ["id"] = s.Id });
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var i in _invites.Where(i => i.Deadline < now).ToList())
            {
                _invites.Remove(i);
                _log.LogInformation("lobby invite #{f} -> #{t} timed out", i.From.Id, i.To.Id);
                Send(i.From, Msg("refuse"));
                Send(i.To, new JsonObject { ["type"] = "cancel_invite", ["id"] = i.From.Id });
            }
            // The client has no timer for the remote player's turn; a frozen opponent would hang it forever.
            foreach (var s in _sessions.Values.Where(x => x.InGame && now - x.LastGameActivity > GameIdleTimeout).ToList())
                if (s.InGame) { EndGame(s, "idle"); Send(s, Msg("game_over")); }
        }
    }

    // ------------------------------------------------------------------ helpers
    private IEnumerable<Session> Others(Session s) => _sessions.Values.Where(x => x.LoggedIn && x != s);

    private JsonObject PlayerJson(Session s) => new()
    {
        ["id"] = s.Id,
        ["nickname"] = s.Nickname,
        ["member_id"] = s.MemberId.ToString(),
        // relative: the client prefixes http://api.vdarts.net:8080/picture/portrait/ and the plugin redirects that
        ["logo"] = s.MemberId + ".png",
        ["nation"] = s.Nation,
        ["auto"] = s.Auto,
        ["version"] = s.Version,
        ["league_id"] = s.LeagueId,
        ["bhv"] = s.InGame ? "fgt" : "idle",
        ["rank"] = new JsonObject { ["average"] = s.Avg },
        ["data"] = new JsonObject { ["isHomeDarts"] = s.HomeDarts, ["isSteelBoard"] = s.SteelBoard, ["Gambling"] = s.Gambling },
    };

    private static string Str(JsonNode n)
    {
        if (n == null) return "";
        if (n is JsonValue v) return v.TryGetValue<string>(out var str) ? str : v.ToJsonString();
        return n.ToJsonString();
    }
    private static string Def(string v, string d) => string.IsNullOrWhiteSpace(v) ? d : v;
    /// <summary>Integer-looking string for fields the client int.Parse()s; anything else becomes "0".</summary>
    private static string Num(string v) => long.TryParse(v, out var n) ? n.ToString() : "0";
    private static string Trunc(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
