using Microsoft.Data.Sqlite;
using RequestBoard.Contracts;

namespace RequestBoard.Service;

public sealed class Database : IDisposable
{
    private const string Columns = "id, requester_id, requester_name, requester_server, text, hours, price, deposit, accepter_id, accepter_name, accepter_server, status, has_location, x, y, z, created_utc, open_expires_utc, accepted_utc, deadline_utc, closed_utc";

    private readonly SqliteConnection _conn;

    public object Sync { get; } = new();

    public Database(string path)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _conn.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");
        Execute("""
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
                open_expires_utc INTEGER NOT NULL,
                accepted_utc INTEGER,
                deadline_utc INTEGER,
                closed_utc INTEGER
            );
            CREATE INDEX IF NOT EXISTS ix_requests_status ON requests(status);
            CREATE INDEX IF NOT EXISTS ix_requests_requester ON requests(requester_id);
            CREATE TABLE IF NOT EXISTS operations (
                op_id TEXT PRIMARY KEY,
                created_utc INTEGER NOT NULL,
                response TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
        PruneOperations();
    }

    public void PruneOperations()
    {
        using var prune = Command(null, "DELETE FROM operations WHERE created_utc < $cutoff", ("$cutoff", DateTime.UtcNow.AddDays(-7).Ticks));
        prune.ExecuteNonQuery();
    }

    public SqliteTransaction Begin() => _conn.BeginTransaction();

    public RequestDto Get(SqliteTransaction tx, int id) =>
        Query(tx, "WHERE id = $id", ("$id", id)).FirstOrDefault();

    public List<RequestDto> Query(SqliteTransaction tx, string where, params (string Name, object Value)[] args)
    {
        using var cmd = Command(tx, $"SELECT {Columns} FROM requests {where}", args);
        using var reader = cmd.ExecuteReader();
        var list = new List<RequestDto>();
        while (reader.Read()) list.Add(Read(reader));
        return list;
    }

    public long Scalar(SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = Command(tx, sql, args);
        var value = cmd.ExecuteScalar();
        return value == null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    public void Insert(SqliteTransaction tx, RequestDto r)
    {
        using var cmd = Command(tx, $"""
            INSERT INTO requests ({Columns.Substring(4)})
            VALUES ($requester_id, $requester_name, $requester_server, $text, $hours, $price, $deposit, $accepter_id, $accepter_name, $accepter_server, $status, $has_location, $x, $y, $z, $created_utc, $open_expires_utc, $accepted_utc, $deadline_utc, $closed_utc);
            SELECT last_insert_rowid();
            """, Parameters(r));
        r.Id = Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Update(SqliteTransaction tx, RequestDto r)
    {
        using var cmd = Command(tx, """
            UPDATE requests SET
                requester_id = $requester_id, requester_name = $requester_name, requester_server = $requester_server,
                text = $text, hours = $hours, price = $price, deposit = $deposit,
                accepter_id = $accepter_id, accepter_name = $accepter_name, accepter_server = $accepter_server,
                status = $status, has_location = $has_location, x = $x, y = $y, z = $z,
                created_utc = $created_utc, open_expires_utc = $open_expires_utc, accepted_utc = $accepted_utc,
                deadline_utc = $deadline_utc, closed_utc = $closed_utc
            WHERE id = $id
            """, Parameters(r).Append(("$id", r.Id)).ToArray());
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Request {r.Id} was not updated.");
    }

    public string FindOperation(SqliteTransaction tx, string opId)
    {
        using var cmd = Command(tx, "SELECT response FROM operations WHERE op_id = $op", ("$op", opId));
        return cmd.ExecuteScalar() as string;
    }

    public void SaveOperation(SqliteTransaction tx, string opId, string response)
    {
        using var cmd = Command(tx, "INSERT INTO operations (op_id, created_utc, response) VALUES ($op, $now, $response)",
            ("$op", opId), ("$now", DateTime.UtcNow.Ticks), ("$response", response));
        cmd.ExecuteNonQuery();
    }

    public string GetSetting(string key)
    {
        using var cmd = Command(null, "SELECT value FROM settings WHERE key = $key", ("$key", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var cmd = Command(null, "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static (string, object)[] Parameters(RequestDto r) =>
    [
        ("$requester_id", r.RequesterId), ("$requester_name", r.RequesterName), ("$requester_server", r.RequesterServer),
        ("$text", r.Text), ("$hours", r.Hours), ("$price", r.Price), ("$deposit", r.Deposit),
        ("$accepter_id", r.AccepterId), ("$accepter_name", r.AccepterName), ("$accepter_server", r.AccepterServer),
        ("$status", (int)r.Status), ("$has_location", r.HasLocation ? 1 : 0), ("$x", r.X), ("$y", r.Y), ("$z", r.Z),
        ("$created_utc", r.CreatedUtc.Ticks), ("$open_expires_utc", r.OpenExpiresUtc.Ticks),
        ("$accepted_utc", r.AcceptedUtc?.Ticks), ("$deadline_utc", r.DeadlineUtc?.Ticks), ("$closed_utc", r.ClosedUtc?.Ticks)
    ];

    private static RequestDto Read(SqliteDataReader rd) => new()
    {
        Id = rd.GetInt32(0),
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
        Status = (RequestStatus)rd.GetInt32(11),
        HasLocation = rd.GetInt32(12) != 0,
        X = rd.GetDouble(13),
        Y = rd.GetDouble(14),
        Z = rd.GetDouble(15),
        CreatedUtc = Utc(rd.GetInt64(16)),
        OpenExpiresUtc = Utc(rd.GetInt64(17)),
        AcceptedUtc = rd.IsDBNull(18) ? null : Utc(rd.GetInt64(18)),
        DeadlineUtc = rd.IsDBNull(19) ? null : Utc(rd.GetInt64(19)),
        ClosedUtc = rd.IsDBNull(20) ? null : Utc(rd.GetInt64(20))
    };

    private static DateTime Utc(long ticks) => new(ticks, DateTimeKind.Utc);
}
