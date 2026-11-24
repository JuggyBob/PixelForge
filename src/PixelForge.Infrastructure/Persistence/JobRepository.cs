using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Infrastructure.Persistence;

public sealed class JobRepository : IJobRepository
{
    private readonly SqliteDatabase _database;
    private readonly IOperationSettingsSerializer _settingsSerializer;
    private readonly ILogger<JobRepository> _logger;

    public JobRepository(SqliteDatabase database, IOperationSettingsSerializer settingsSerializer,
        ILogger<JobRepository> logger)
    {
        _database = database;
        _settingsSerializer = settingsSerializer;
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
                CREATE TABLE IF NOT EXISTS Jobs (
                    Id TEXT PRIMARY KEY,
                    Operation INTEGER NOT NULL,
                    Title TEXT NOT NULL,
                    State INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    StartedAt TEXT NULL,
                    CompletedAt TEXT NULL,
                    SettingsJson TEXT NOT NULL,
                    EngineId TEXT NULL,
                    OutputDirectory TEXT NOT NULL,
                    Error TEXT NULL,
                    ErrorDetails TEXT NULL,
                    SourcePath TEXT NULL,
                    PreserveRelativePaths INTEGER NOT NULL DEFAULT 0,
                    BatchSummary TEXT NULL,
                    PipelineId TEXT NULL,
                    PipelineName TEXT NULL,
                    PipelineStep INTEGER NOT NULL DEFAULT 0,
                    PipelineStepCount INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS JobItems (
                    Id TEXT PRIMARY KEY,
                    JobId TEXT NOT NULL,
                    InputPath TEXT NOT NULL,
                    FileName TEXT NOT NULL,
                    RelativeDirectory TEXT NULL,
                    PreferredOutputName TEXT NULL,
                    OutputPath TEXT NULL,
                    TempPath TEXT NULL,
                    State INTEGER NOT NULL,
                    Progress REAL NOT NULL,
                    Message TEXT NULL,
                    Error TEXT NULL,
                    ErrorDetails TEXT NULL,
                    ErrorCategory TEXT NULL,
                    EngineLog TEXT NULL,
                    RequestedProvider TEXT NULL,
                    ActualProvider TEXT NULL,
                    FallbackReason TEXT NULL,
                    RuntimeVersion TEXT NULL,
                    ModelId TEXT NULL,
                    SkipReason TEXT NULL,
                    StartedAt TEXT NULL,
                    CompletedAt TEXT NULL,
                    DurationMs REAL NULL,
                    InputBytes INTEGER NOT NULL,
                    OutputBytes INTEGER NULL,
                    Attempt INTEGER NOT NULL,
                    InputInfoJson TEXT NULL,
                    OutputInfoJson TEXT NULL,
                    OutputFormatId TEXT NULL,
                    AdditionalOutputPathsJson TEXT NULL,
                    WarningsJson TEXT NULL,
                    VerificationSummary TEXT NULL,
                    IsBatchChild INTEGER NOT NULL,
                    SortOrder INTEGER NOT NULL,
                    FOREIGN KEY(JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_Jobs_CreatedAt ON Jobs(CreatedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_Jobs_State ON Jobs(State);
                CREATE INDEX IF NOT EXISTS IX_JobItems_JobId ON JobItems(JobId, SortOrder);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            foreach (var (name, definition) in new[]
                     {
                         ("ErrorCategory", "TEXT NULL"),
                         ("EngineLog", "TEXT NULL"),
                         ("RequestedProvider", "TEXT NULL"),
                         ("ActualProvider", "TEXT NULL"),
                         ("FallbackReason", "TEXT NULL"),
                         ("RuntimeVersion", "TEXT NULL"),
                         ("ModelId", "TEXT NULL"),
                         ("PreferredOutputName", "TEXT NULL"),
                         ("AdditionalOutputPathsJson", "TEXT NULL")
                     })
                await EnsureItemColumnAsync(connection, transaction, name, definition, cancellationToken);

            foreach (var (name, definition) in new[]
                     {
                         ("PipelineId", "TEXT NULL"),
                         ("PipelineName", "TEXT NULL"),
                         ("PipelineStep", "INTEGER NOT NULL DEFAULT 0"),
                         ("PipelineStepCount", "INTEGER NOT NULL DEFAULT 0")
                     })
                await EnsureColumnAsync(connection, transaction, "Jobs", name, definition, cancellationToken);
        }, cancellationToken);
    }

    private static Task EnsureItemColumnAsync(SqliteConnection connection, SqliteTransaction transaction,
        string name, string definition, CancellationToken cancellationToken) =>
        EnsureColumnAsync(connection, transaction, "JobItems", name, definition, cancellationToken);

    private static async Task EnsureColumnAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string name, string definition, CancellationToken cancellationToken)
    {
        var exists = false;
        await using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = $"PRAGMA table_info({table});";
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
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(ProcessingJob job, CancellationToken cancellationToken = default)
    {
        await _database.WriteAsync(async (connection, transaction) =>
        {
            await UpsertJobAsync(connection, transaction, job, cancellationToken);
            foreach (var item in job.Items)
                await UpsertItemAsync(connection, transaction, item, cancellationToken);
        }, cancellationToken);
    }

    public async Task SaveItemAsync(ProcessingJob job, JobItem item, CancellationToken cancellationToken = default)
    {
        await _database.WriteAsync(async (connection, transaction) =>
        {
            await UpsertJobAsync(connection, transaction, job, cancellationToken);
            await UpsertItemAsync(connection, transaction, item, cancellationToken);
        }, cancellationToken);
    }

    public async Task<ProcessingJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _database.ReadAsync(async connection =>
        {
            ProcessingJob? job = null;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM Jobs WHERE Id=$id";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                    job = ReadJob(reader);
            }

            if (job is null) return null;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM JobItems WHERE JobId=$id ORDER BY SortOrder, FileName";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    job.Items.Add(ReadItem(reader));
            }

            return job;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ProcessingJob>> GetRecentAsync(int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return await _database.ReadAsync(async connection =>
        {
            var jobs = new List<ProcessingJob>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM Jobs ORDER BY CreatedAt DESC LIMIT $limit";
                command.Parameters.AddWithValue("$limit", limit);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    jobs.Add(ReadJob(reader));
            }

            if (jobs.Count == 0) return jobs;
            var byId = jobs.ToDictionary(j => j.Id);
            var placeholders = string.Join(",", byId.Keys.Select((_, i) => "$p" + i));
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT * FROM JobItems WHERE JobId IN ({placeholders}) ORDER BY SortOrder, FileName";
                var index = 0;
                foreach (var id in byId.Keys)
                    command.Parameters.AddWithValue("$p" + index++, id.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var item = ReadItem(reader);
                    if (byId.TryGetValue(item.JobId, out var job)) job.Items.Add(item);
                }
            }

            return jobs;
        }, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Jobs WHERE Id=$id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task DeleteTerminalAsync(CancellationToken cancellationToken = default) =>
        await DeleteByStatesAsync([JobState.Completed, JobState.Failed, JobState.Cancelled, JobState.Interrupted], cancellationToken);

    public async Task<int> DeleteByStatesAsync(IReadOnlyCollection<JobState> states,
        CancellationToken cancellationToken = default)
    {
        if (states.Count == 0) return 0;
        return await _database.WriteAsync(async (connection, transaction) =>
        {
            var stateValues = string.Join(",", states.Select(s => ((int)s).ToString(CultureInfo.InvariantCulture)));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM Jobs WHERE State IN ({stateValues})";
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        var count = await _database.WriteAsync(async (connection, transaction) =>
        {
            await using var itemCommand = connection.CreateCommand();
            itemCommand.Transaction = transaction;
            itemCommand.CommandText = """
                UPDATE JobItems
                SET State=$cancelled, Progress=1.0, CompletedAt=$now,
                    Message='Interrupted by application shutdown', Error='The application closed before this item completed.'
                WHERE State IN ($pending, $running)
                """;
            itemCommand.Parameters.AddWithValue("$cancelled", (int)JobItemState.Cancelled);
            itemCommand.Parameters.AddWithValue("$pending", (int)JobItemState.Pending);
            itemCommand.Parameters.AddWithValue("$running", (int)JobItemState.Running);
            itemCommand.Parameters.AddWithValue("$now", now);
            await itemCommand.ExecuteNonQueryAsync(cancellationToken);

            await using var jobCommand = connection.CreateCommand();
            jobCommand.Transaction = transaction;
            jobCommand.CommandText = """
                UPDATE Jobs
                SET State=$interrupted, CompletedAt=$now,
                    Error=COALESCE(Error, 'The application closed before this job completed.')
                WHERE State IN ($pending, $preparing, $running)
                """;
            jobCommand.Parameters.AddWithValue("$interrupted", (int)JobState.Interrupted);
            jobCommand.Parameters.AddWithValue("$pending", (int)JobState.Pending);
            jobCommand.Parameters.AddWithValue("$preparing", (int)JobState.Preparing);
            jobCommand.Parameters.AddWithValue("$running", (int)JobState.Running);
            jobCommand.Parameters.AddWithValue("$now", now);
            var affected = await jobCommand.ExecuteNonQueryAsync(cancellationToken);
            return affected;
        }, cancellationToken);

        if (count > 0) _logger.LogWarning("Recovered {Count} interrupted jobs", count);
        return count;
    }

    private async Task UpsertJobAsync(SqliteConnection connection, SqliteTransaction transaction,
        ProcessingJob job, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Jobs (Id, Operation, Title, State, CreatedAt, StartedAt, CompletedAt, SettingsJson,
                EngineId, OutputDirectory, Error, ErrorDetails, SourcePath, PreserveRelativePaths, BatchSummary,
                PipelineId, PipelineName, PipelineStep, PipelineStepCount)
            VALUES ($id, $operation, $title, $state, $created, $started, $completed, $settings,
                $engine, $output, $error, $details, $source, $preserve, $batch,
                $pipelineId, $pipelineName, $pipelineStep, $pipelineStepCount)
            ON CONFLICT(Id) DO UPDATE SET
                State=excluded.State,
                StartedAt=excluded.StartedAt,
                CompletedAt=excluded.CompletedAt,
                SettingsJson=excluded.SettingsJson,
                EngineId=excluded.EngineId,
                OutputDirectory=excluded.OutputDirectory,
                Error=excluded.Error,
                ErrorDetails=excluded.ErrorDetails,
                BatchSummary=excluded.BatchSummary,
                PipelineId=excluded.PipelineId,
                PipelineName=excluded.PipelineName,
                PipelineStep=excluded.PipelineStep,
                PipelineStepCount=excluded.PipelineStepCount
            """;
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        command.Parameters.AddWithValue("$operation", (int)job.Operation);
        command.Parameters.AddWithValue("$title", job.Title);
        command.Parameters.AddWithValue("$state", (int)job.State);
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$started", (object?)job.StartedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", (object?)job.CompletedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$settings", _settingsSerializer.Serialize(job.Settings));
        command.Parameters.AddWithValue("$engine", (object?)job.EngineId ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", job.OutputDirectory ?? "");
        command.Parameters.AddWithValue("$error", (object?)job.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", (object?)job.ErrorDetails ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)job.SourcePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$preserve", job.PreserveRelativePaths ? 1 : 0);
        command.Parameters.AddWithValue("$batch", (object?)job.BatchSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$pipelineId", (object?)job.PipelineId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$pipelineName", (object?)job.PipelineName ?? DBNull.Value);
        command.Parameters.AddWithValue("$pipelineStep", job.PipelineStep);
        command.Parameters.AddWithValue("$pipelineStepCount", job.PipelineStepCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertItemAsync(SqliteConnection connection, SqliteTransaction transaction,
        JobItem item, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO JobItems (Id, JobId, InputPath, FileName, RelativeDirectory, PreferredOutputName,
                OutputPath, TempPath, State, Progress, Message, Error, ErrorDetails, ErrorCategory, EngineLog,
                RequestedProvider, ActualProvider, FallbackReason, RuntimeVersion, ModelId, SkipReason, StartedAt,
                CompletedAt, DurationMs, InputBytes, OutputBytes, Attempt, InputInfoJson, OutputInfoJson,
                OutputFormatId, AdditionalOutputPathsJson, WarningsJson, VerificationSummary, IsBatchChild, SortOrder)
            VALUES ($id, $jobId, $input, $fileName, $relative, $preferred, $output, $temp, $state, $progress,
                $message, $error, $details, $errorCategory, $engineLog, $requestedProvider, $actualProvider,
                $fallback, $runtime, $model, $skip, $started, $completed, $duration, $inputBytes, $outputBytes,
                $attempt, $inputInfo, $outputInfo, $format, $additional, $warnings, $verification, $batchChild, $sort)
            ON CONFLICT(Id) DO UPDATE SET
                OutputPath=excluded.OutputPath,
                TempPath=excluded.TempPath,
                State=excluded.State,
                Progress=excluded.Progress,
                Message=excluded.Message,
                Error=excluded.Error,
                ErrorDetails=excluded.ErrorDetails,
                ErrorCategory=excluded.ErrorCategory,
                EngineLog=excluded.EngineLog,
                RequestedProvider=excluded.RequestedProvider,
                ActualProvider=excluded.ActualProvider,
                FallbackReason=excluded.FallbackReason,
                RuntimeVersion=excluded.RuntimeVersion,
                ModelId=excluded.ModelId,
                SkipReason=excluded.SkipReason,
                StartedAt=excluded.StartedAt,
                CompletedAt=excluded.CompletedAt,
                DurationMs=excluded.DurationMs,
                OutputBytes=excluded.OutputBytes,
                Attempt=excluded.Attempt,
                InputInfoJson=excluded.InputInfoJson,
                OutputInfoJson=excluded.OutputInfoJson,
                OutputFormatId=excluded.OutputFormatId,
                AdditionalOutputPathsJson=excluded.AdditionalOutputPathsJson,
                WarningsJson=excluded.WarningsJson,
                VerificationSummary=excluded.VerificationSummary
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$jobId", item.JobId.ToString("D"));
        command.Parameters.AddWithValue("$input", item.InputPath);
        command.Parameters.AddWithValue("$fileName", item.FileName);
        command.Parameters.AddWithValue("$relative", (object?)item.RelativeDirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("$preferred", (object?)item.PreferredOutputName ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", (object?)item.OutputPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$temp", (object?)item.TempPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", (int)item.State);
        command.Parameters.AddWithValue("$progress", item.Progress);
        command.Parameters.AddWithValue("$message", (object?)item.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)item.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", (object?)item.ErrorDetails ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCategory", (object?)item.ErrorCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$engineLog", (object?)item.EngineLog ?? DBNull.Value);
        command.Parameters.AddWithValue("$requestedProvider", (object?)item.RequestedProvider ?? DBNull.Value);
        command.Parameters.AddWithValue("$actualProvider", (object?)item.ActualProvider ?? DBNull.Value);
        command.Parameters.AddWithValue("$fallback", (object?)item.FallbackReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$runtime", (object?)item.RuntimeVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)item.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$skip", (object?)item.SkipReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", (object?)item.StartedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", (object?)item.CompletedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)item.Duration?.TotalMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$inputBytes", item.InputBytes);
        command.Parameters.AddWithValue("$outputBytes", (object?)item.OutputBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempt", item.Attempt);
        command.Parameters.AddWithValue("$inputInfo", (object?)PersistenceJson.Serialize(item.InputInfo) ?? DBNull.Value);
        command.Parameters.AddWithValue("$outputInfo", (object?)PersistenceJson.Serialize(item.OutputInfo) ?? DBNull.Value);
        command.Parameters.AddWithValue("$format", (object?)item.OutputFormatId ?? DBNull.Value);
        command.Parameters.AddWithValue("$additional",
            (object?)PersistenceJson.Serialize(item.AdditionalOutputPaths) ?? DBNull.Value);
        command.Parameters.AddWithValue("$warnings", (object?)PersistenceJson.Serialize(item.Warnings) ?? DBNull.Value);
        command.Parameters.AddWithValue("$verification", (object?)item.VerificationSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$batchChild", item.IsBatchChild ? 1 : 0);
        command.Parameters.AddWithValue("$sort", item.SortOrder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private ProcessingJob ReadJob(SqliteDataReader reader)
    {
        var operation = (OperationKind)reader.GetInt32(reader.GetOrdinal("Operation"));
        var settingsJson = reader.GetString(reader.GetOrdinal("SettingsJson"));
        return new ProcessingJob
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            Operation = operation,
            Title = reader.GetString(reader.GetOrdinal("Title")),
            State = (JobState)reader.GetInt32(reader.GetOrdinal("State")),
            CreatedAt = ParseDate(reader, "CreatedAt") ?? DateTimeOffset.Now,
            StartedAt = ParseDate(reader, "StartedAt"),
            CompletedAt = ParseDate(reader, "CompletedAt"),
            Settings = _settingsSerializer.Deserialize(operation, settingsJson),
            EngineId = GetNullableString(reader, "EngineId"),
            OutputDirectory = reader.GetString(reader.GetOrdinal("OutputDirectory")),
            Error = GetNullableString(reader, "Error"),
            ErrorDetails = GetNullableString(reader, "ErrorDetails"),
            SourcePath = GetNullableString(reader, "SourcePath"),
            PreserveRelativePaths = reader.GetInt32(reader.GetOrdinal("PreserveRelativePaths")) != 0,
            BatchSummary = GetNullableString(reader, "BatchSummary"),
            PipelineId = GetNullableGuid(reader, "PipelineId"),
            PipelineName = GetNullableString(reader, "PipelineName"),
            PipelineStep = reader.IsDBNull(reader.GetOrdinal("PipelineStep"))
                ? 0
                : reader.GetInt32(reader.GetOrdinal("PipelineStep")),
            PipelineStepCount = reader.IsDBNull(reader.GetOrdinal("PipelineStepCount"))
                ? 0
                : reader.GetInt32(reader.GetOrdinal("PipelineStepCount"))
        };
    }

    private static Guid? GetNullableGuid(SqliteDataReader reader, string column)
    {
        var value = GetNullableString(reader, column);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private static JobItem ReadItem(SqliteDataReader reader)
    {
        var state = (JobItemState)reader.GetInt32(reader.GetOrdinal("State"));
        return new JobItem
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            JobId = Guid.Parse(reader.GetString(reader.GetOrdinal("JobId"))),
            InputPath = reader.GetString(reader.GetOrdinal("InputPath")),
            FileName = reader.GetString(reader.GetOrdinal("FileName")),
            RelativeDirectory = GetNullableString(reader, "RelativeDirectory"),
            PreferredOutputName = GetNullableString(reader, "PreferredOutputName"),
            OutputPath = GetNullableString(reader, "OutputPath"),
            TempPath = GetNullableString(reader, "TempPath"),
            State = state,
            Progress = reader.GetDouble(reader.GetOrdinal("Progress")),
            Message = GetNullableString(reader, "Message"),
            Error = GetNullableString(reader, "Error"),
            ErrorDetails = GetNullableString(reader, "ErrorDetails"),
            ErrorCategory = GetNullableString(reader, "ErrorCategory"),
            EngineLog = GetNullableString(reader, "EngineLog"),
            RequestedProvider = GetNullableString(reader, "RequestedProvider"),
            ActualProvider = GetNullableString(reader, "ActualProvider"),
            FallbackReason = GetNullableString(reader, "FallbackReason"),
            RuntimeVersion = GetNullableString(reader, "RuntimeVersion"),
            ModelId = GetNullableString(reader, "ModelId"),
            SkipReason = GetNullableString(reader, "SkipReason"),
            StartedAt = ParseDate(reader, "StartedAt"),
            CompletedAt = ParseDate(reader, "CompletedAt"),
            Duration = reader.IsDBNull(reader.GetOrdinal("DurationMs"))
                ? null
                : TimeSpan.FromMilliseconds(reader.GetDouble(reader.GetOrdinal("DurationMs"))),
            InputBytes = reader.GetInt64(reader.GetOrdinal("InputBytes")),
            OutputBytes = reader.IsDBNull(reader.GetOrdinal("OutputBytes"))
                ? null
                : reader.GetInt64(reader.GetOrdinal("OutputBytes")),
            Attempt = reader.GetInt32(reader.GetOrdinal("Attempt")),
            InputInfo = PersistenceJson.Deserialize<MediaInfo>(GetNullableString(reader, "InputInfoJson")),
            OutputInfo = PersistenceJson.Deserialize<MediaInfo>(GetNullableString(reader, "OutputInfoJson")),
            OutputFormatId = GetNullableString(reader, "OutputFormatId"),
            AdditionalOutputPaths = PersistenceJson.Deserialize<List<string>>(
                GetNullableString(reader, "AdditionalOutputPathsJson")) ?? [],
            Warnings = PersistenceJson.Deserialize<List<string>>(GetNullableString(reader, "WarningsJson")) ?? [],
            VerificationSummary = GetNullableString(reader, "VerificationSummary"),
            IsBatchChild = reader.GetInt32(reader.GetOrdinal("IsBatchChild")) != 0,
            SortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder"))
        };
    }

    private static DateTimeOffset? ParseDate(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return null;
        return DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
