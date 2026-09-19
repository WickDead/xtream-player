using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace XtreamPlayer.Infrastructure.Persistence;

public sealed record MigrationScript(int Version, string Name, string Sql);

public sealed class MigrationException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Forward-only, transactional schema migrations.
/// </summary>
/// <remarks>
/// The previous app had a single <c>INITIAL_MIGRATION</c> string and no version
/// tracking at all, so every schema change had to be a hand-written
/// <c>CREATE TABLE IF NOT EXISTS</c> bolted on at startup. This replaces that.
///
/// Each script runs inside one transaction that also records its version row, so
/// a failure leaves the database exactly as it was.
/// </remarks>
public sealed partial class SqliteMigrator
{
    private readonly Assembly _assembly;
    private readonly string _resourcePrefix;

    public SqliteMigrator(Assembly? assembly = null, string? resourcePrefix = null)
    {
        _assembly = assembly ?? typeof(SqliteMigrator).Assembly;
        _resourcePrefix = resourcePrefix
            ?? "XtreamPlayer.Infrastructure.Persistence.Migrations.";
    }

    [GeneratedRegex(@"^(?<version>\d+)_(?<name>.+)\.sql$", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptNamePattern { get; }

    /// <summary>All embedded scripts, ordered by version.</summary>
    public IReadOnlyList<MigrationScript> LoadScripts()
    {
        var scripts = new List<MigrationScript>();

        foreach (var resource in _assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(_resourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resource[_resourcePrefix.Length..];
            var match = ScriptNamePattern.Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            using var stream = _assembly.GetManifestResourceStream(resource)
                ?? throw new MigrationException($"Could not read migration resource '{resource}'.");
            using var reader = new StreamReader(stream);

            scripts.Add(new MigrationScript(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                reader.ReadToEnd()));
        }

        var duplicate = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new MigrationException(
                $"Two migrations share version {duplicate.Key}: "
              + string.Join(", ", duplicate.Select(s => s.Name)));
        }

        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        return scripts;
    }

    public int GetCurrentVersion(SqliteConnection connection)
    {
        EnsureVersionTable(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Applies every migration newer than the database's current version.
    /// Idempotent: running twice applies nothing the second time.
    /// </summary>
    /// <returns>The versions actually applied.</returns>
    public IReadOnlyList<int> Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        ConfigurePragmas(connection);
        EnsureVersionTable(connection);

        var current = GetCurrentVersion(connection);
        var applied = new List<int>();

        foreach (var script in LoadScripts().Where(s => s.Version > current))
        {
            // One transaction per script, including its version row, so a
            // failure rolls the schema back rather than leaving it half-built.
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = script.Sql;
                    command.ExecuteNonQuery();
                }

                using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText =
                        "INSERT INTO schema_version (version, name, applied_at) VALUES ($v, $n, $t);";
                    record.Parameters.AddWithValue("$v", script.Version);
                    record.Parameters.AddWithValue("$n", script.Name);
                    record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    record.ExecuteNonQuery();
                }

                transaction.Commit();
                applied.Add(script.Version);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                throw new MigrationException(
                    $"Migration {script.Version} ({script.Name}) failed and was rolled back. "
                  + "The database is unchanged.", ex);
            }
        }

        if (applied.Count > 0)
        {
            // Mirrored as a cheap secondary check; schema_version stays authoritative.
            using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA user_version = {applied[^1]};";
            pragma.ExecuteNonQuery();
        }

        return applied;
    }

    private static void ConfigurePragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -64000;
            PRAGMA foreign_keys = ON;
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureVersionTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version    INTEGER NOT NULL PRIMARY KEY,
                name       TEXT,
                applied_at TEXT    NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
