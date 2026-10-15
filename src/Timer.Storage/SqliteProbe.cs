using Microsoft.Data.Sqlite;

namespace Timer.Storage;

// The first step of the move to a database: proof that SQLite loads and works inside the published build, before anything depends on it.
// It opens a throwaway file, writes in a transaction, reads it back, and reports what version of SQLite it ran on.
public static class SqliteProbe
{
    public static string Check()
    {
        var folder = Path.Combine(Path.GetTempPath(), "timer-sqlite-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "probe.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                Run(connection, "PRAGMA journal_mode=WAL");
                Run(connection, "CREATE TABLE probe (id INTEGER PRIMARY KEY, seconds INTEGER NOT NULL, note TEXT)");
                using (var transaction = connection.BeginTransaction())
                {
                    Run(connection, "INSERT INTO probe (seconds, note) VALUES (5400, 'ninety minutes')", transaction);
                    Run(connection, "INSERT INTO probe (seconds, note) VALUES (-600, NULL)", transaction);
                    transaction.Commit();
                }
                using var sum = connection.CreateCommand(); sum.CommandText = "SELECT SUM(seconds) FROM probe";
                if (Convert.ToInt64(sum.ExecuteScalar()) != 4800) throw new InvalidDataException("SQLite returned the wrong total.");
                using var version = connection.CreateCommand(); version.CommandText = "SELECT sqlite_version()";
                return (string)version.ExecuteScalar()!;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
    private static void Run(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; command.ExecuteNonQuery();
    }
}
