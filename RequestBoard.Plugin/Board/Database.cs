using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NLog;

namespace RequestBoard.Board
{
    public sealed class Database : IDisposable
    {
        private const string Columns = "id, requester_id, requester_name, requester_server, text, hours, price, deposit, accepter_id, accepter_name, accepter_server, status, has_location, x, y, z, created_utc, expires_utc, accepted_utc, closed_utc";
        private const string SettingsKey = "board";
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly Func<string> _configuredPath;
        private readonly string _storagePath;
        private SQLiteConnection _conn;
        private string _openPath;

        public object Sync { get; } = new object();

        public Database(Func<string> configuredPath, string storagePath)
        {
            _configuredPath = configuredPath;
            _storagePath = storagePath;
        }

        public string ResolvePath()
        {
            var configured = (_configuredPath() ?? "").Trim();
            if (configured.Length == 0) throw new InvalidOperationException("The database path is not set.");
            return Path.GetFullPath(Path.Combine(_storagePath, Environment.ExpandEnvironmentVariables(configured)));
        }

        public string EnsureOpen()
        {
            var path = ResolvePath();
            if (_conn != null && string.Equals(path, _openPath, StringComparison.OrdinalIgnoreCase)) return path;
            Close();

            NativeSqlite.EnsureLoaded(_storagePath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var conn = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 15 }.ToString());
            try
            {
                conn.Open();
                Execute(conn, "PRAGMA busy_timeout=15000;");
                Execute(conn, "PRAGMA journal_mode=WAL;");
                Execute(conn, "PRAGMA synchronous=FULL;");
                Execute(conn, @"
                    CREATE TABLE IF NOT EXISTS requests (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        requester_id INTEGER NOT NULL,
                        requester_name TEXT NOT NULL,
                        requester_server TEXT,
                        text TEXT NOT NULL,
                        hours REAL NOT NULL,
                        price INTEGER NOT NULL,
                        deposit INTEGER NOT NULL,
                        accepter_id INTEGER NOT NULL DEFAULT 0,
                        accepter_name TEXT,
                        accepter_server TEXT,
                        status INTEGER NOT NULL,
                        has_location INTEGER NOT NULL,
                        x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL,
                        created_utc INTEGER NOT NULL,
                        expires_utc INTEGER NOT NULL,
                        accepted_utc INTEGER,
                        closed_utc INTEGER
                    );
                    CREATE INDEX IF NOT EXISTS ix_requests_status ON requests(status);
                    CREATE INDEX IF NOT EXISTS ix_requests_requester ON requests(requester_id);
                    CREATE TABLE IF NOT EXISTS settings (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL
                    );");
                using (var seed = Command(conn, null, "INSERT OR IGNORE INTO settings (key, value) VALUES (@key, @value)",
                           ("@key", SettingsKey), ("@value", JsonConvert.SerializeObject(new BoardSettings()))))
                    seed.ExecuteNonQuery();
            }
            catch
            {
                conn.Dispose();
                throw;
            }

            _conn = conn;
            _openPath = path;
            Log.Info($"RequestBoard: opened database {path}");
            return path;
        }

        public SQLiteTransaction BeginWrite()
        {
            EnsureOpen();
            return _conn.BeginTransaction(IsolationLevel.Serializable);
        }

        public SQLiteTransaction BeginRead()
        {
            EnsureOpen();
            return _conn.BeginTransaction(IsolationLevel.ReadCommitted);
        }

        public BoardRequest Get(SQLiteTransaction tx, int id) =>
            Query(tx, "WHERE id = @id", ("@id", id)).FirstOrDefault();

        public List<BoardRequest> Query(SQLiteTransaction tx, string where, params (string Name, object Value)[] args)
        {
            using (var cmd = Command(tx, $"SELECT {Columns} FROM requests {where}", args))
            using (var reader = cmd.ExecuteReader())
            {
                var list = new List<BoardRequest>();
                while (reader.Read()) list.Add(Read(reader));
                return list;
            }
        }

        public long Scalar(SQLiteTransaction tx, string sql, params (string Name, object Value)[] args)
        {
            using (var cmd = Command(tx, sql, args))
            {
                var value = cmd.ExecuteScalar();
                return value == null || value is DBNull ? 0 : Convert.ToInt64(value);
            }
        }

        public void Insert(SQLiteTransaction tx, BoardRequest r)
        {
            using (var cmd = Command(tx, $@"
                INSERT INTO requests ({Columns.Substring(4)})
                VALUES (@requester_id, @requester_name, @requester_server, @text, @hours, @price, @deposit, @accepter_id, @accepter_name, @accepter_server, @status, @has_location, @x, @y, @z, @created_utc, @expires_utc, @accepted_utc, @closed_utc);
                SELECT last_insert_rowid();", Parameters(r)))
                r.Id = Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void Update(SQLiteTransaction tx, BoardRequest r)
        {
            using (var cmd = Command(tx, @"
                UPDATE requests SET
                    requester_id = @requester_id, requester_name = @requester_name, requester_server = @requester_server,
                    text = @text, hours = @hours, price = @price, deposit = @deposit,
                    accepter_id = @accepter_id, accepter_name = @accepter_name, accepter_server = @accepter_server,
                    status = @status, has_location = @has_location, x = @x, y = @y, z = @z,
                    created_utc = @created_utc, expires_utc = @expires_utc, accepted_utc = @accepted_utc, closed_utc = @closed_utc
                WHERE id = @id", Parameters(r).Append(("@id", (object)r.Id)).ToArray()))
            {
                if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Request {r.Id} was not updated.");
            }
        }

        public BoardSettings LoadSettings(SQLiteTransaction tx)
        {
            using (var cmd = Command(tx, "SELECT value FROM settings WHERE key = @key", ("@key", SettingsKey)))
            {
                var stored = cmd.ExecuteScalar() as string;
                return stored == null ? new BoardSettings() : JsonConvert.DeserializeObject<BoardSettings>(stored) ?? new BoardSettings();
            }
        }

        public void SaveSettings(SQLiteTransaction tx, BoardSettings settings)
        {
            using (var cmd = Command(tx, "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                       ("@key", SettingsKey), ("@value", JsonConvert.SerializeObject(settings))))
                cmd.ExecuteNonQuery();
        }

        public void Close()
        {
            _conn?.Dispose();
            _conn = null;
            _openPath = null;
        }

        public void Dispose()
        {
            lock (Sync) Close();
        }

        private static void Execute(SQLiteConnection conn, string sql)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private SQLiteCommand Command(SQLiteTransaction tx, string sql, params (string Name, object Value)[] args) =>
            Command(tx?.Connection ?? _conn, tx, sql, args);

        private static SQLiteCommand Command(SQLiteConnection conn, SQLiteTransaction tx, string sql, params (string Name, object Value)[] args)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd;
        }

        private static (string, object)[] Parameters(BoardRequest r) => new (string, object)[]
        {
            ("@requester_id", r.RequesterId), ("@requester_name", r.RequesterName), ("@requester_server", r.RequesterServer),
            ("@text", r.Text), ("@hours", r.Hours), ("@price", r.Price), ("@deposit", r.Deposit),
            ("@accepter_id", r.AccepterId), ("@accepter_name", r.AccepterName), ("@accepter_server", r.AccepterServer),
            ("@status", (int)r.Status), ("@has_location", r.HasLocation ? 1 : 0), ("@x", r.X), ("@y", r.Y), ("@z", r.Z),
            ("@created_utc", r.CreatedUtc.Ticks), ("@expires_utc", r.ExpiresUtc.Ticks),
            ("@accepted_utc", r.AcceptedUtc?.Ticks), ("@closed_utc", r.ClosedUtc?.Ticks)
        };

        private static BoardRequest Read(SQLiteDataReader rd) => new BoardRequest
        {
            Id = (int)rd.GetInt64(0),
            RequesterId = rd.GetInt64(1),
            RequesterName = rd.GetString(2),
            RequesterServer = rd.IsDBNull(3) ? null : rd.GetString(3),
            Text = rd.GetString(4),
            Hours = rd.GetDouble(5),
            Price = rd.GetInt64(6),
            Deposit = rd.GetInt64(7),
            AccepterId = rd.GetInt64(8),
            AccepterName = rd.IsDBNull(9) ? null : rd.GetString(9),
            AccepterServer = rd.IsDBNull(10) ? null : rd.GetString(10),
            Status = (RequestStatus)rd.GetInt64(11),
            HasLocation = rd.GetInt64(12) != 0,
            X = rd.GetDouble(13),
            Y = rd.GetDouble(14),
            Z = rd.GetDouble(15),
            CreatedUtc = Utc(rd.GetInt64(16)),
            ExpiresUtc = Utc(rd.GetInt64(17)),
            AcceptedUtc = rd.IsDBNull(18) ? (DateTime?)null : Utc(rd.GetInt64(18)),
            ClosedUtc = rd.IsDBNull(19) ? (DateTime?)null : Utc(rd.GetInt64(19))
        };

        private static DateTime Utc(long ticks) => new DateTime(ticks, DateTimeKind.Utc);
    }
}
