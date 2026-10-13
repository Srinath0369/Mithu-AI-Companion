using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mithu.Infrastructure.Persistence;

/// <summary>
/// Mithu's local database (<c>mithu.db</c>). Opened lazily on first use, so nothing is loaded until a feature
/// needs it. On first open it:
/// <list type="number">
/// <item>checks the file; a corrupt or unreadable file is set aside as <c>mithu.db.corrupt-…</c> and a fresh one is
///   created (<see cref="WasRecovered"/> tells the UI);</item>
/// <item>switches to WAL (fast, crash-safe writes) and turns foreign keys on;</item>
/// <item>backs up the file and applies any pending <see cref="Migrations"/> (schema version in <c>PRAGMA user_version</c>).</item>
/// </list>
/// Thread-safe: each call to <see cref="Open"/> returns a new pooled connection.
/// </summary>
public sealed class SqliteDatabase(string path, ILogger<SqliteDatabase> logger)
{
    private readonly Lock _gate = new();
    private string? _connectionString;

    /// <summary>The database file was unreadable and has been replaced with a fresh one (the old file was kept).</summary>
    public bool WasRecovered { get; private set; }

    public string Path => path;

    /// <summary>An open connection to an up-to-date database. Dispose it when done.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(EnsureReady());
        connection.Open();
        return connection;
    }

    private string EnsureReady()
    {
        if (_connectionString is { } ready)
        {
            return ready;
        }

        lock (_gate)
        {
            if (_connectionString is null)
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    ForeignKeys = true,
                    Pooling = true,
                }.ToString();

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                if (File.Exists(path) && !IsHealthy(connectionString))
                {
                    SetAsideCorruptFile();
                }

                Migrate(connectionString);
                _connectionString = connectionString;
            }

            return _connectionString;
        }
    }

    private bool IsHealthy(string connectionString)
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = command.ExecuteScalar() as string;
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            logger.LogError("Database check failed: {Result}", result);
            return false;
        }
        catch (SqliteException ex)
        {
            logger.LogError(ex, "Database could not be opened");
            return false;
        }
    }

    private void SetAsideCorruptFile()
    {
        SqliteConnection.ClearAllPools();
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = path + suffix;
            if (File.Exists(file))
            {
                File.Move(file, $"{path}.corrupt-{stamp}{suffix}", overwrite: true);
            }
        }

        WasRecovered = true;
        logger.LogWarning("Corrupt database set aside; starting a fresh one");
    }

    private void Migrate(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");

        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        if (version >= Migrations.Scripts.Count)
        {
            return;
        }

        if (version > 0)
        {
            Backup(connection, version);
        }

        for (var next = version; next < Migrations.Scripts.Count; next++)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, Migrations.Scripts[next], transaction);
            Execute(connection, $"PRAGMA user_version = {next + 1};", transaction);
            transaction.Commit();
            logger.LogInformation("Database migrated to v{Version}", next + 1);
        }
    }

    /// <summary>Copies the database before changing its schema: <c>mithu.db.bak-v1</c>.</summary>
    private void Backup(SqliteConnection connection, int version)
    {
        var backupPath = $"{path}.bak-v{version}";
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString());
        connection.BackupDatabase(backup);
        logger.LogInformation("Database backed up before migration: {Backup}", backupPath);
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
