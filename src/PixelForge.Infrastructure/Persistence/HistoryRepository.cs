using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Infrastructure.Persistence;

public sealed class HistoryRepository : IHistoryRepository
{
    private readonly SqliteDatabase _database;
    private readonly ILogger<HistoryRepository> _logger;

    public HistoryRepository(SqliteDatabase database, ILogger<HistoryRepository> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken);
        await _database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS History (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    JobId TEXT NOT NULL,
                    ItemId TEXT NOT NULL,
                    Operation INTEGER NOT NULL,
                    Title TEXT NOT NULL,
                    InputPath TEXT NULL,
                    OutputPath TEXT NULL,
                    InputFileName TEXT NULL,
                    OutputFileName TEXT NULL,
                    InputBytes INTEGER NOT NULL DEFAULT 0,
                    OutputBytes INTEGER NULL,
                    FormatId TEXT NULL,
                    SettingsJson TEXT NULL,
                    DurationMs REAL NOT NULL DEFAULT 0,
                    Status INTEGER NOT NULL,
                    Error TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    VerificationSummary TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_History_CreatedAt ON History(CreatedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_History_Operation ON History(Operation);
                CREATE INDEX IF NOT EXISTS IX_History_FileName ON History(InputFileName);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
        _logger.LogInformation("History database initialized");
    }

    public async Task<long> AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        return await _database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO History (JobId, ItemId, Operation, Title, InputPath, OutputPath, InputFileName,
                    OutputFileName, InputBytes, OutputBytes, FormatId, SettingsJson, DurationMs, Status, Error,
                    CreatedAt, VerificationSummary)
                VALUES ($jobId, $itemId, $operation, $title, $input, $output, $inputName, $outputName,
                    $inputBytes, $outputBytes, $format, $settings, $duration, $status, $error, $created, $verification);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$jobId", entry.JobId.ToString("D"));
            command.Parameters.AddWithValue("$itemId", entry.ItemId.ToString("D"));
            command.Parameters.AddWithValue("$operation", (int)entry.Operation);
            command.Parameters.AddWithValue("$title", entry.Title);
            command.Parameters.AddWithValue("$input", (object?)entry.InputPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$output", (object?)entry.OutputPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$inputName", (object?)entry.InputFileName ?? DBNull.Value);
            command.Parameters.AddWithValue("$outputName", (object?)entry.OutputFileName ?? DBNull.Value);
            command.Parameters.AddWithValue("$inputBytes", entry.InputBytes);
            command.Parameters.AddWithValue("$outputBytes", (object?)entry.OutputBytes ?? DBNull.Value);
            command.Parameters.AddWithValue("$format", (object?)entry.FormatId ?? DBNull.Value);
            command.Parameters.AddWithValue("$settings", (object?)entry.SettingsJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$duration", entry.DurationMs);
            command.Parameters.AddWithValue("$status", (int)entry.Status);
            command.Parameters.AddWithValue("$error", (object?)entry.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$verification", (object?)entry.VerificationSummary ?? DBNull.Value);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int limit = 200, string? search = null,
        OperationKind? operation = null, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 5000);
        return await _database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("(Title LIKE $search OR InputFileName LIKE $search OR OutputFileName LIKE $search OR InputPath LIKE $search)");
                command.Parameters.AddWithValue("$search", $"%{search.Trim()}%");
            }

            if (operation is not null)
            {
                conditions.Add("Operation=$operation");
                command.Parameters.AddWithValue("$operation", (int)operation.Value);
            }

            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
            command.CommandText = $"SELECT * FROM History {where} ORDER BY CreatedAt DESC, Id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            var entries = new List<HistoryEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(ReadEntry(reader));
            return (IReadOnlyList<HistoryEntry>)entries;
        }, cancellationToken);
    }

    public async Task<HistoryEntry?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        return await _database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM History WHERE Id=$id";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
        }, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM History";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        return await _database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM History WHERE CreatedAt < $cutoff";
            command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<int> TrimAsync(int maxEntries, CancellationToken cancellationToken = default)
    {
        maxEntries = Math.Max(1, maxEntries);
        return await _database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM History
                WHERE Id NOT IN (SELECT Id FROM History ORDER BY CreatedAt DESC, Id DESC LIMIT $max)
                """;
            command.Parameters.AddWithValue("$max", maxEntries);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        return await _database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM History";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    private static HistoryEntry ReadEntry(SqliteDataReader reader)
    {
        return new HistoryEntry
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            JobId = Guid.Parse(reader.GetString(reader.GetOrdinal("JobId"))),
            ItemId = Guid.Parse(reader.GetString(reader.GetOrdinal("ItemId"))),
            Operation = (OperationKind)reader.GetInt32(reader.GetOrdinal("Operation")),
            Title = reader.GetString(reader.GetOrdinal("Title")),
            InputPath = GetNullableString(reader, "InputPath"),
            OutputPath = GetNullableString(reader, "OutputPath"),
            InputFileName = GetNullableString(reader, "InputFileName"),
            OutputFileName = GetNullableString(reader, "OutputFileName"),
            InputBytes = reader.GetInt64(reader.GetOrdinal("InputBytes")),
            OutputBytes = reader.IsDBNull(reader.GetOrdinal("OutputBytes")) ? null : reader.GetInt64(reader.GetOrdinal("OutputBytes")),
            FormatId = GetNullableString(reader, "FormatId"),
            SettingsJson = GetNullableString(reader, "SettingsJson"),
            DurationMs = reader.GetDouble(reader.GetOrdinal("DurationMs")),
            Status = (JobItemState)reader.GetInt32(reader.GetOrdinal("Status")),
            Error = GetNullableString(reader, "Error"),
            CreatedAt = DateTimeOffset.TryParse(GetNullableString(reader, "CreatedAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var d) ? d : DateTimeOffset.MinValue,
            VerificationSummary = GetNullableString(reader, "VerificationSummary")
        };
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
