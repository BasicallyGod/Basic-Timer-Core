using Microsoft.Data.Sqlite;

namespace Timer.Storage;

public sealed class DatabaseInUseException : IOException
{
    public DatabaseInUseException(string path, Exception inner) : base("The timer's data is already open in another copy of the timer (" + path + "). Close the other copy first.", inner) { }
}
public sealed class DatabaseTooNewException : IOException
{
    public DatabaseTooNewException(int found, int known) : base($"This timer's data was made by a newer version of the timer (data format {found}, this version understands up to {known}). Update the timer, or use a copy of the data from before the change.") { }
}

// The timer's single data file. It holds every record the app keeps (see the data format proposal) and nothing about preferences.
// It is opened exclusively, so a second copy of the timer cannot write to it, and every change goes through a transaction so a
// change either lands whole or not at all.
public sealed class TimerDatabase : IDisposable
{
    public const int SchemaVersion = 3;
    public const string FileName = "timer.db";
    public const int DailyCopiesKept = 14;
    // Numbered upgrades from one format to the next: entry 0 takes the data from format 1 to 2, entry 1 from 2 to 3, and so on. A build that adds a table
    // or column adds one here and raises SchemaVersion; nothing rewrites the data in place without a copy being saved first.
    // Format 2 allows a budget per day. The accounts table carries a CHECK on the budget period that SQLite cannot widen in place, so the
    // table is rebuilt, with foreign keys off while it happens (SQLite ignores that switch inside a transaction, so Upgrade sets it) and checked before the change is kept.
    public static string AccountsTable(string name, bool days) => $@"CREATE TABLE {name} (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE CHECK (length(name) > 0),
            colour TEXT NOT NULL CHECK (colour GLOB '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'),
            position INTEGER NOT NULL,
            hidden INTEGER NOT NULL DEFAULT 0 CHECK (hidden IN (0, 1)),
            budget_seconds INTEGER CHECK (budget_seconds IS NULL OR budget_seconds > 0),
            budget_period TEXT NOT NULL DEFAULT 'week' CHECK (budget_period IN ({(days ? "'day', " : "")}'week', 'month', 'quarter')))";
    public const string DayNotesTable = @"CREATE TABLE day_notes (
            id INTEGER PRIMARY KEY,
            day TEXT NOT NULL,
            at_time TEXT,
            text TEXT NOT NULL CHECK (length(text) > 0),
            done INTEGER NOT NULL DEFAULT 0 CHECK (done IN (0, 1)))";
    public const string DayNotesIndex = "CREATE INDEX day_notes_by_day ON day_notes(day)";
    public static readonly IReadOnlyList<string> Upgrades = new[]
    {
        AccountsTable("accounts_next", true) + "; "
        + "INSERT INTO accounts_next (id, name, colour, position, hidden, budget_seconds, budget_period) SELECT id, name, colour, position, hidden, budget_seconds, budget_period FROM accounts; "
        + "DROP TABLE accounts; ALTER TABLE accounts_next RENAME TO accounts;",
        // Format 3 adds notes on a day, with an optional time. Nothing existing is touched.
        DayNotesTable + "; " + DayNotesIndex
    };

    private readonly SqliteConnection connection;
    private SqliteTransaction? transaction;
    private readonly string folder;
    public string Path { get; }
    public int Version { get; private set; }

    private TimerDatabase(string folder, SqliteConnection connection)
    { this.folder = folder; this.connection = connection; Path = System.IO.Path.Combine(folder, FileName); }

    /// <param name="upgrades">Steps from format n to n + 1 as SQL, used by tests to exercise the upgrade path.</param>
    public static TimerDatabase Open(string folder, Func<DateTime>? clock = null, IReadOnlyList<string>? upgrades = null, int? targetVersion = null)
    {
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, FileName);
        var steps = upgrades ?? Upgrades; var target = targetVersion ?? SchemaVersion;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        TimerDatabase? database = null;
        try
        {
            connection.Open();
            try
            {
                // One writer at a time, kept for as long as the timer runs, so a second copy is turned away instead of racing the first.
                Pragma(connection, "locking_mode=EXCLUSIVE");
                Pragma(connection, "journal_mode=WAL");
                // Taking the lock is lazy, so write once to claim it now.
                Pragma(connection, "synchronous=NORMAL"); Pragma(connection, "foreign_keys=ON"); Pragma(connection, "busy_timeout=2000");
                Exec(connection, null, "BEGIN IMMEDIATE"); Exec(connection, null, "COMMIT");
            }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { throw new DatabaseInUseException(path, error); }
            database = new TimerDatabase(folder, connection);
            database.Version = Convert.ToInt32(database.Scalar("PRAGMA user_version"));
            if (database.Version > target) throw new DatabaseTooNewException(database.Version, target);
            if (database.Version == 0) database.Create();
            else if (database.Version < target) database.Upgrade(steps, target, clock ?? (() => DateTime.Now));
            database.DailyCopy(clock ?? (() => DateTime.Now));
            return database;
        }
        catch
        {
            if (database is not null) database.Dispose(); else { connection.Dispose(); SqliteConnection.ClearAllPools(); }
            throw;
        }
    }

    private void Create()
    {
        InTransaction(() =>
        {
            foreach (var sql in Schema) Execute(sql);
            Execute($"PRAGMA user_version = {SchemaVersion}");
        });
        Version = SchemaVersion;
    }
    private void Upgrade(IReadOnlyList<string> steps, int target, Func<DateTime> clock)
    {
        // The data is copied aside first, so a failed or unwanted upgrade can be put back.
        var copy = System.IO.Path.Combine(folder, "Backups", $"timer-before-format-{target}-{clock():yyyyMMdd-HHmmss}.db");
        CopyTo(copy);
        // Rebuilding a table that others point at needs foreign keys off, which only takes effect outside a transaction.
        // They are checked again before the change is kept, and switched back on whatever happens.
        Execute("PRAGMA foreign_keys = OFF");
        try
        {
            InTransaction(() =>
            {
                for (var from = Version; from < target; from++)
                {
                    if (from - 1 >= steps.Count) throw new InvalidDataException("No upgrade is defined from data format " + from + ".");
                    Execute(steps[from - 1]);
                }
                if (Query("PRAGMA foreign_key_check", r => 1).Count > 0) throw new InvalidDataException("The upgrade left records pointing at accounts that do not exist, so it was undone.");
                Execute($"PRAGMA user_version = {target}");
            });
        }
        finally { Execute("PRAGMA foreign_keys = ON"); }
        Version = target;
    }
    // The first launch each day saves a dated copy, since the file cannot be repaired by hand. The newest few are kept.
    private void DailyCopy(Func<DateTime> clock)
    {
        var backups = System.IO.Path.Combine(folder, "Backups");
        var today = System.IO.Path.Combine(backups, $"timer-{clock():yyyyMMdd}.db");
        if (!File.Exists(today)) CopyTo(today);
        foreach (var old in Directory.GetFiles(backups, "timer-????????.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(DailyCopiesKept))
            File.Delete(old);
    }
    public void CopyTo(string target)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.Delete(target);
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path"; command.Parameters.AddWithValue("$path", target); command.ExecuteNonQuery();
    }

    // ---- Small helpers, so callers write SQL and nothing about connections ----
    public void InTransaction(Action work)
    {
        if (transaction is not null) { work(); return; } // Already inside one: join it, so the outer change stays whole.
        transaction = connection.BeginTransaction();
        try { work(); transaction.Commit(); }
        catch { transaction.Rollback(); throw; }
        finally { transaction.Dispose(); transaction = null; }
    }
    public T InTransaction<T>(Func<T> work) { T result = default!; InTransaction((Action)(() => { result = work(); })); return result; }
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(sql, parameters); return command.ExecuteNonQuery(); } // Commands are disposed so the file is free to delete once the database closes (Windows keeps it locked otherwise).
    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(sql, parameters); return command.ExecuteScalar(); }
    public long Insert(string sql, params (string Name, object? Value)[] parameters)
    { Execute(sql, parameters); return Convert.ToInt64(Scalar("SELECT last_insert_rowid()")); }
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters); using var reader = command.ExecuteReader();
        var rows = new List<T>(); while (reader.Read()) rows.Add(read(reader)); return rows;
    }
    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static void Pragma(SqliteConnection connection, string setting) => Exec(connection, null, "PRAGMA " + setting);
    private static void Exec(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; command.ExecuteNonQuery(); }

    /// <summary>Writes everything into the one file and closes it, so a copy of the folder always holds a complete database.</summary>
    public void Dispose()
    {
        try
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                transaction?.Dispose(); transaction = null;
                Exec(connection, null, "PRAGMA wal_checkpoint(TRUNCATE)");
                Exec(connection, null, "PRAGMA journal_mode=DELETE"); // Leaves no -wal or -shm file beside the database.
            }
        }
        catch (SqliteException) { }
        finally { connection.Dispose(); SqliteConnection.ClearAllPools(); }
    }

    private static readonly string[] Schema =
    {
        AccountsTable("accounts", true),
        @"CREATE TABLE groups (
            id INTEGER PRIMARY KEY,
            account_id INTEGER NOT NULL REFERENCES accounts(id),
            day TEXT NOT NULL,
            note TEXT NOT NULL DEFAULT '')",
        // Each kind says which columns it must and must not have, so a malformed entry cannot be stored at all.
        @"CREATE TABLE entries (
            id INTEGER PRIMARY KEY,
            kind TEXT NOT NULL CHECK (kind IN ('work', 'break', 'adjustment')),
            account_id INTEGER REFERENCES accounts(id),
            day TEXT NOT NULL,
            start_at TEXT,
            end_at TEXT,
            seconds INTEGER NOT NULL,
            origin TEXT NOT NULL DEFAULT 'recorded' CHECK (origin IN ('recorded', 'added', 'edited')),
            note TEXT NOT NULL DEFAULT '',
            group_id INTEGER REFERENCES groups(id) ON DELETE SET NULL,
            created_at TEXT NOT NULL,
            -- The duration exactly as the current undo history wrote it. Temporary: step 4 replaces that history and this column goes.
            minutes_text TEXT,
            CHECK (kind <> 'work' OR (account_id IS NOT NULL AND start_at IS NOT NULL AND end_at IS NOT NULL AND seconds > 0)),
            CHECK (kind <> 'break' OR (account_id IS NULL AND start_at IS NOT NULL AND end_at IS NOT NULL AND seconds > 0)),
            -- Adjustments were removed from the app. Older databases may still hold such rows, and the store drops them when it opens one.
            CHECK (kind <> 'adjustment' OR (end_at IS NULL AND seconds <> 0)))",
        "CREATE INDEX entries_by_day ON entries(day)",
        "CREATE INDEX entries_by_account ON entries(account_id, day)",
        DayNotesTable,
        DayNotesIndex,
        @"CREATE TABLE away (
            id INTEGER PRIMARY KEY,
            start_at TEXT NOT NULL,
            end_at TEXT)",
        @"CREATE TABLE day_starts (
            day TEXT PRIMARY KEY,
            start_at TEXT NOT NULL)",
        // At most one recording runs at a time. A break also remembers the account to go back to.
        @"CREATE TABLE running (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            kind TEXT NOT NULL CHECK (kind IN ('work', 'break')),
            account_id INTEGER REFERENCES accounts(id),
            resume_account_id INTEGER REFERENCES accounts(id),
            start_at TEXT NOT NULL,
            last_seen TEXT NOT NULL,
            CHECK (kind <> 'work' OR account_id IS NOT NULL),
            CHECK (kind <> 'break' OR account_id IS NULL))",
        // Small named blobs for what has not yet moved into real tables (notes, groups, credit positions and the undo history).
        @"CREATE TABLE meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL)",
        @"CREATE TABLE edits (
            id INTEGER PRIMARY KEY,
            at TEXT NOT NULL,
            description TEXT NOT NULL)",
        @"CREATE TABLE edit_changes (
            id INTEGER PRIMARY KEY,
            edit_id INTEGER NOT NULL REFERENCES edits(id) ON DELETE CASCADE,
            table_name TEXT NOT NULL,
            row_id INTEGER NOT NULL,
            before_json TEXT,
            after_json TEXT)",
    };
}
