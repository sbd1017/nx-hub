using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NxHub.Server.Storage;

/// <summary>The NX Hub SQLite database: connection factory and forward-only migrations.</summary>
public sealed class SqliteDatabase
{
    private static readonly string[] Migrations =
    [
        // v1: configured Switches
        """
        CREATE TABLE switches (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            host        TEXT NOT NULL,
            port        INTEGER NOT NULL,
            enabled     INTEGER NOT NULL,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );
        """,
        // v2: saved macros (definition is JSON)
        """
        CREATE TABLE macros (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            definition  TEXT NOT NULL,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );
        """,
    ];

    private readonly string _connectionString;

    public SqliteDatabase(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Initialise()
    {
        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        var current = Convert.ToInt32(read.ExecuteScalar(), CultureInfo.InvariantCulture);

        for (var version = current; version < Migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, Migrations[version], transaction);
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_version (version) VALUES ($v);";
            record.Parameters.AddWithValue("$v", version + 1);
            record.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
