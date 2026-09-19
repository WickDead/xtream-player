using Microsoft.Data.Sqlite;

namespace XtreamPlayer.Infrastructure.Persistence;

public interface IDbConnectionFactory
{
    SqliteConnection Open();
}

/// <summary>
/// Opens connections to the application database, running migrations once on
/// first use.
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly SqliteMigrator _migrator;
    private readonly Lock _gate = new();
    private bool _migrated;

    public SqliteConnectionFactory(string? databaseFile = null, SqliteMigrator? migrator = null)
    {
        DatabaseFile = databaseFile ?? DatabasePaths.DatabaseFile;
        _migrator = migrator ?? new SqliteMigrator();

        var directory = Path.GetDirectoryName(DatabaseFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // WAL plus a shared cache is the wrong pairing; keep connections
            // private and let WAL handle concurrency.
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabaseFile { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Pragmas are per-connection, not per-database, so they are re-applied
        // on every open. Only foreign_keys and the busy timeout actually matter
        // here; journal_mode is persistent and set during migration.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                """;
            pragma.ExecuteNonQuery();
        }

        EnsureMigrated(connection);
        return connection;
    }

    private void EnsureMigrated(SqliteConnection connection)
    {
        if (_migrated)
        {
            return;
        }

        lock (_gate)
        {
            if (_migrated)
            {
                return;
            }

            _migrator.Migrate(connection);
            _migrated = true;
        }
    }
}
