using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PixelForge.Domain.Interfaces;

namespace PixelForge.Infrastructure.Persistence;

public sealed class SqliteDatabase
{
    private readonly string _databasePath;
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _connectionString;

    public SqliteDatabase(IAppPaths paths, ILogger<SqliteDatabase> logger)
    {
        _databasePath = paths.DatabaseFile;
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=10000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("SQLite database ready at {Path}", _databasePath);
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await action(connection);
    }

    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await action(connection, (SqliteTransaction)transaction);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task WriteAsync(Func<SqliteConnection, SqliteTransaction, Task> action,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(async (connection, transaction) =>
        {
            await action(connection, transaction);
            return true;
        }, cancellationToken);
    }
}

internal static class PersistenceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string? Serialize<T>(T? value) =>
        value is null ? null : JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch
        {
            return default;
        }
    }
}
