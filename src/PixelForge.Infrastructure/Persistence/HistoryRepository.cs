using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Infrastructure.Persistence;

public sealed class HistoryRepository : IHistoryRepository
{
    private const int CurrentSchemaVersion = 2;
    private const int MaxImportEntries = 100_000;

    private readonly SqliteDatabase _database;
    private readonly ILogger<HistoryRepository> _logger;
    private readonly object _initializationLock = new();
    private Task? _initializationTask;

    public HistoryRepository(SqliteDatabase database, ILogger<HistoryRepository> logger)
    {
        _database = database;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_initializationLock)
        {
            task = _initializationTask ??= InitializeCoreAsync();
        }
        return task.WaitAsync(cancellationToken);
    }

    private async Task InitializeCoreAsync()
    {
        await _database.InitializeAsync(CancellationToken.None);
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
                    ErrorDetails TEXT NULL,
                    ErrorCategory TEXT NULL,
                    EngineLog TEXT NULL,
                    WarningsJson TEXT NULL,
                    RequestedProvider TEXT NULL,
                    ActualProvider TEXT NULL,
                    FallbackReason TEXT NULL,
                    RuntimeVersion TEXT NULL,
                    ModelId TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    VerificationSummary TEXT NULL,
                    PipelineId TEXT NULL,
                    PipelineName TEXT NULL,
                    PipelineStep INTEGER NOT NULL DEFAULT 0,
                    PipelineStepCount INTEGER NOT NULL DEFAULT 0
                );
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);

            foreach (var (name, definition) in new[]
                     {
                         ("ErrorDetails", "TEXT NULL"),
                         ("ErrorCategory", "TEXT NULL"),
                         ("EngineLog", "TEXT NULL"),
                         ("WarningsJson", "TEXT NULL"),
                         ("RequestedProvider", "TEXT NULL"),
                         ("ActualProvider", "TEXT NULL"),
                         ("FallbackReason", "TEXT NULL"),
                         ("RuntimeVersion", "TEXT NULL"),
                         ("ModelId", "TEXT NULL"),
                         ("PipelineId", "TEXT NULL"),
                         ("PipelineName", "TEXT NULL"),
                         ("PipelineStep", "INTEGER NOT NULL DEFAULT 0"),
                         ("PipelineStepCount", "INTEGER NOT NULL DEFAULT 0")
                     })
                await EnsureColumnAsync(connection, transaction, name, definition, CancellationToken.None);

            await using var indexes = connection.CreateCommand();
            indexes.Transaction = transaction;
            indexes.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_History_CreatedAt ON History(CreatedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_History_Operation ON History(Operation);
                CREATE INDEX IF NOT EXISTS IX_History_FileName ON History(InputFileName);
                CREATE INDEX IF NOT EXISTS IX_History_Status ON History(Status);
                CREATE INDEX IF NOT EXISTS IX_History_FormatId ON History(FormatId);
                CREATE INDEX IF NOT EXISTS IX_History_ModelId ON History(ModelId);
                PRAGMA user_version = 2;
                """;
            await indexes.ExecuteNonQueryAsync(CancellationToken.None);
        }, CancellationToken.None);
        _logger.LogInformation("History database initialized at schema {Schema}", CurrentSchemaVersion);
    }

    public async Task<long> AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _database.WriteAsync(async (connection, transaction) =>
        {
            await InsertAsync(connection, transaction, entry, cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT last_insert_rowid();";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int limit = 200, string? search = null,
        OperationKind? operation = null, CancellationToken cancellationToken = default) =>
        await QueryAsync(new HistoryQuery { Limit = limit, Search = search, Operation = operation }, cancellationToken);

    public async Task<IReadOnlyList<HistoryEntry>> QueryAsync(HistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var limit = Math.Clamp(query.Limit, 1, MaxImportEntries);
        return await _database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                conditions.Add("""
                    (Title LIKE $search OR InputFileName LIKE $search OR OutputFileName LIKE $search OR
                     InputPath LIKE $search OR Error LIKE $search OR ErrorDetails LIKE $search OR
                     ModelId LIKE $search OR RequestedProvider LIKE $search OR ActualProvider LIKE $search)
                    """);
                command.Parameters.AddWithValue("$search", $"%{query.Search.Trim()}%");
            }

            if (query.Operation is not null)
            {
                conditions.Add("Operation=$operation");
                command.Parameters.AddWithValue("$operation", (int)query.Operation.Value);
            }

            if (query.Status is not null)
            {
                conditions.Add("Status=$status");
                command.Parameters.AddWithValue("$status", (int)query.Status.Value);
            }

            if (!string.IsNullOrWhiteSpace(query.FormatId))
            {
                conditions.Add("FormatId=$formatId");
                command.Parameters.AddWithValue("$formatId", query.FormatId.Trim());
            }

            if (!string.IsNullOrWhiteSpace(query.ModelId))
            {
                conditions.Add("ModelId LIKE $modelId");
                command.Parameters.AddWithValue("$modelId", $"%{query.ModelId.Trim()}%");
            }

            if (!string.IsNullOrWhiteSpace(query.Provider))
            {
                conditions.Add("(RequestedProvider LIKE $provider OR ActualProvider LIKE $provider)");
                command.Parameters.AddWithValue("$provider", $"%{query.Provider.Trim()}%");
            }

            if (query.From is not null)
            {
                conditions.Add("CreatedAt >= $from");
                command.Parameters.AddWithValue("$from", query.From.Value.ToString("O", CultureInfo.InvariantCulture));
            }

            if (query.To is not null)
            {
                conditions.Add("CreatedAt <= $to");
                command.Parameters.AddWithValue("$to", query.To.Value.ToString("O", CultureInfo.InvariantCulture));
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

    public async Task<HistoryExportDocument> ExportAsync(HistoryQuery query, string appVersion,
        CancellationToken cancellationToken = default)
    {
        var entries = await QueryAsync(query with { Limit = Math.Clamp(query.Limit, 1, MaxImportEntries) }, cancellationToken);
        return new HistoryExportDocument
        {
            SchemaVersion = 1,
            AppVersion = appVersion,
            ExportedAt = DateTimeOffset.Now,
            Entries = entries
        };
    }

    public async Task<HistoryImportResult> ImportAsync(HistoryExportDocument document,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (document.SchemaVersion != 1)
            return new HistoryImportResult
            {
                Invalid = document.Entries.Count,
                Warnings = [$"Unsupported history schema version {document.SchemaVersion}. Expected 1."]
            };

        if (document.Entries.Count > MaxImportEntries)
            return new HistoryImportResult
            {
                Invalid = document.Entries.Count,
                Warnings = [$"Import contains {document.Entries.Count} records; the maximum is {MaxImportEntries}."]
            };

        return await _database.WriteAsync(async (connection, transaction) =>
        {
            var imported = 0;
            var skipped = 0;
            var invalid = 0;
            var warnings = new List<string>();
            foreach (var entry in document.Entries)
            {
                var error = ValidateImportEntry(entry);
                if (error is not null)
                {
                    invalid++;
                    if (warnings.Count < 20) warnings.Add(error);
                    continue;
                }

                if (await ExistsAsync(connection, transaction, entry.JobId, entry.ItemId, cancellationToken))
                {
                    skipped++;
                    continue;
                }

                await InsertAsync(connection, transaction, entry, cancellationToken);
                imported++;
            }

            return new HistoryImportResult
            {
                Imported = imported,
                Skipped = skipped,
                Invalid = invalid,
                Warnings = warnings
            };
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
            await command.ExecuteNonQueryAsync(CancellationToken.None);
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
        await InitializeAsync(cancellationToken);
        return await _database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM History";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, SqliteTransaction transaction,
        string name, string definition, CancellationToken cancellationToken)
    {
        var exists = false;
        await using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "PRAGMA table_info(History);";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists) return;
        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE History ADD COLUMN {name} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ExistsAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid jobId, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM History WHERE JobId=$jobId AND ItemId=$itemId";
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$itemId", itemId.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction,
        HistoryEntry entry, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO History (JobId, ItemId, Operation, Title, InputPath, OutputPath, InputFileName,
                OutputFileName, InputBytes, OutputBytes, FormatId, SettingsJson, DurationMs, Status, Error,
                ErrorDetails, ErrorCategory, EngineLog, WarningsJson, RequestedProvider, ActualProvider,
                FallbackReason, RuntimeVersion, ModelId, CreatedAt, VerificationSummary,
                PipelineId, PipelineName, PipelineStep, PipelineStepCount)
            VALUES ($jobId, $itemId, $operation, $title, $input, $output, $inputName, $outputName,
                $inputBytes, $outputBytes, $format, $settings, $duration, $status, $error, $errorDetails,
                $errorCategory, $engineLog, $warnings, $requestedProvider, $actualProvider, $fallback,
                $runtime, $model, $created, $verification,
                $pipelineId, $pipelineName, $pipelineStep, $pipelineStepCount);
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
        command.Parameters.AddWithValue("$errorDetails", (object?)entry.ErrorDetails ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCategory", (object?)entry.ErrorCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$engineLog", (object?)entry.EngineLog ?? DBNull.Value);
        command.Parameters.AddWithValue("$warnings", (object?)entry.WarningsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$requestedProvider", (object?)entry.RequestedProvider ?? DBNull.Value);
        command.Parameters.AddWithValue("$actualProvider", (object?)entry.ActualProvider ?? DBNull.Value);
        command.Parameters.AddWithValue("$fallback", (object?)entry.FallbackReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$runtime", (object?)entry.RuntimeVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)entry.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$verification", (object?)entry.VerificationSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$pipelineId", (object?)entry.PipelineId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$pipelineName", (object?)entry.PipelineName ?? DBNull.Value);
        command.Parameters.AddWithValue("$pipelineStep", entry.PipelineStep);
        command.Parameters.AddWithValue("$pipelineStepCount", entry.PipelineStepCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? ValidateImportEntry(HistoryEntry entry)
    {
        if (entry.JobId == Guid.Empty || entry.ItemId == Guid.Empty) return "Record has an empty job or item id.";
        if (string.IsNullOrWhiteSpace(entry.Title)) return "Record has no title.";
        if (!Enum.IsDefined(entry.Operation)) return "Record has an invalid operation.";
        if (!Enum.IsDefined(entry.Status)) return "Record has an invalid status.";
        if (entry.CreatedAt == DateTimeOffset.MinValue) return "Record has no creation date.";
        if (entry.Title.Length > 4096) return "Record title is too long.";
        if (entry.InputPath?.Length > 32768 || entry.OutputPath?.Length > 32768) return "Record path is too long.";
        if (entry.ErrorDetails?.Length > 200_000 || entry.EngineLog?.Length > 200_000) return "Record diagnostic text is too long.";
        if (entry.WarningsJson?.Length > 1_000_000) return "Record warnings are too large.";
        return null;
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
            ErrorDetails = GetNullableString(reader, "ErrorDetails"),
            ErrorCategory = GetNullableString(reader, "ErrorCategory"),
            EngineLog = GetNullableString(reader, "EngineLog"),
            WarningsJson = GetNullableString(reader, "WarningsJson"),
            RequestedProvider = GetNullableString(reader, "RequestedProvider"),
            ActualProvider = GetNullableString(reader, "ActualProvider"),
            FallbackReason = GetNullableString(reader, "FallbackReason"),
            RuntimeVersion = GetNullableString(reader, "RuntimeVersion"),
            ModelId = GetNullableString(reader, "ModelId"),
            CreatedAt = DateTimeOffset.TryParse(GetNullableString(reader, "CreatedAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var d) ? d : DateTimeOffset.MinValue,
            VerificationSummary = GetNullableString(reader, "VerificationSummary"),
            PipelineId = Guid.TryParse(GetNullableString(reader, "PipelineId"), out var pipelineId)
                ? pipelineId
                : null,
            PipelineName = GetNullableString(reader, "PipelineName"),
            PipelineStep = reader.IsDBNull(reader.GetOrdinal("PipelineStep"))
                ? 0
                : reader.GetInt32(reader.GetOrdinal("PipelineStep")),
            PipelineStepCount = reader.IsDBNull(reader.GetOrdinal("PipelineStepCount"))
                ? 0
                : reader.GetInt32(reader.GetOrdinal("PipelineStepCount"))
        };
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
