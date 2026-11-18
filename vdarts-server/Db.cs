using Microsoft.Data.Sqlite;

namespace VDartsServer;

/// <summary>Tiny SQLite helper. One connection per call; SQLite + WAL handles the concurrency we need.</summary>
public sealed class Db
{
    private readonly string _cs;
    public Db(string path)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        Exec("PRAGMA journal_mode=WAL;");
        Migrate();
    }

    public SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    private static void Bind(SqliteCommand cmd, object[] args)
    {
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + (i + 1), args[i] ?? DBNull.Value);
    }

    public int Exec(string sql, params object[] args)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = sql; Bind(cmd, args); return cmd.ExecuteNonQuery();
    }

    public long Insert(string sql, params object[] args)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = sql + "; SELECT last_insert_rowid();"; Bind(cmd, args);
        return (long)cmd.ExecuteScalar();
    }

    public object Scalar(string sql, params object[] args)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = sql; Bind(cmd, args);
        var r = cmd.ExecuteScalar(); return r is DBNull ? null : r;
    }

    public List<Dictionary<string, object>> Query(string sql, params object[] args)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = sql; Bind(cmd, args);
        using var r = cmd.ExecuteReader();
        var list = new List<Dictionary<string, object>>();
        while (r.Read())
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(d);
        }
        return list;
    }

    public Dictionary<string, object> Row(string sql, params object[] args) => Query(sql, args).FirstOrDefault();

    private void Migrate()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS members(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  account TEXT NOT NULL UNIQUE,
  password TEXT NOT NULL DEFAULT '',
  nickname TEXT NOT NULL,
  nation TEXT NOT NULL DEFAULT 'TW',
  email TEXT NOT NULL DEFAULT '',
  is_locked INTEGER NOT NULL DEFAULT 0,
  vch INTEGER NOT NULL DEFAULT 0,
  gft INTEGER NOT NULL DEFAULT 0,
  vpoint INTEGER NOT NULL DEFAULT 0,
  rank01_override TEXT,
  rankcr_override TEXT,
  has_logo INTEGER NOT NULL DEFAULT 0,
  logo BLOB,
  created TEXT NOT NULL,
  last_login TEXT
);
-- start member ids at 1001 (ids 1..9 look like guests to the client: guests upload member_id '9')
INSERT OR IGNORE INTO sqlite_sequence(name, seq) SELECT 'members', 1000 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name='members');
CREATE TABLE IF NOT EXISTS cards(
  card_no TEXT PRIMARY KEY,
  member_id INTEGER NOT NULL REFERENCES members(id),
  created TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS tokens(
  token TEXT PRIMARY KEY,
  member_id INTEGER NOT NULL,
  mac TEXT,
  created TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS machines(
  mac TEXT PRIMARY KEY,
  name TEXT NOT NULL DEFAULT '',
  company_name TEXT NOT NULL DEFAULT '',
  dealer_psw TEXT NOT NULL DEFAULT '',
  is_locked INTEGER NOT NULL DEFAULT 0,
  lock_after TEXT NOT NULL DEFAULT '2099-12-31 00:00:00',
  coin_insert INTEGER NOT NULL DEFAULT 0,
  coin_consume INTEGER NOT NULL DEFAULT 0,
  prices TEXT,
  rounds TEXT,
  client_ip TEXT,
  first_seen TEXT NOT NULL,
  last_seen TEXT
);
CREATE TABLE IF NOT EXISTS game_keys(
  card_no TEXT PRIMARY KEY,
  mac TEXT NOT NULL DEFAULT '',
  note TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS records(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  game_type INTEGER NOT NULL,
  group_by INTEGER,
  member_id INTEGER NOT NULL,
  mac TEXT,
  rating REAL,
  score INTEGER,
  effect TEXT,
  record TEXT,
  league_id TEXT,
  winner INTEGER,
  is_winner INTEGER,
  created TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_records_member ON records(member_id, game_type);
CREATE INDEX IF NOT EXISTS ix_records_game ON records(game_type, created);
CREATE TABLE IF NOT EXISTS raw_uploads(
  id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT, body TEXT, created TEXT
);
CREATE TABLE IF NOT EXISTS feedback(
  id INTEGER PRIMARY KEY AUTOINCREMENT, member_id TEXT, type TEXT, opinion TEXT, photo BLOB, mac TEXT, created TEXT
);
CREATE TABLE IF NOT EXISTS announcements(
  id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, content TEXT NOT NULL, update_time TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS leagues(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  mac TEXT NOT NULL DEFAULT '',
  season TEXT NOT NULL DEFAULT '1',
  name TEXT NOT NULL,
  team_name1 TEXT NOT NULL DEFAULT '',
  team_name2 TEXT NOT NULL DEFAULT '',
  detail TEXT NOT NULL DEFAULT '{}',
  created TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS league_results(
  id INTEGER PRIMARY KEY AUTOINCREMENT, league_id TEXT, mac TEXT, data TEXT, created TEXT
);
CREATE TABLE IF NOT EXISTS businesses(
  acc TEXT PRIMARY KEY, pass TEXT NOT NULL, email TEXT NOT NULL DEFAULT '', box_mac TEXT NOT NULL DEFAULT '', created TEXT
);
CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
");
        if (Scalar("SELECT COUNT(*) FROM announcements") is long n && n == 0)
            Exec("INSERT INTO announcements(title, content, update_time) VALUES($1,$2,$3)",
                "Welcome back", "This cabinet is connected to a community-hosted VDarts server.", Util.Now());
    }

    public string Setting(string key, string def = null) => Scalar("SELECT value FROM settings WHERE key=$1", key) as string ?? def;
    public void SetSetting(string key, string value) => Exec("INSERT INTO settings(key,value) VALUES($1,$2) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
}
