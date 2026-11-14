using Microsoft.Extensions.Logging.Abstractions;
using PixelForge.Domain;
using PixelForge.Domain.Models;
using PixelForge.Infrastructure.Persistence;
using PixelForge.Infrastructure.Settings;
using Xunit;

namespace PixelForge.Tests.Integration;

public sealed class PersistenceTests
{
    [Fact]
    public async Task Settings_are_persisted_and_reloaded()
    {
        using var paths = new TempAppPaths();
        var first = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await first.LoadAsync();
        Assert.Equal(2, first.Settings.Processing.MaxConcurrentJobs);

        await first.UpdateAsync(settings =>
        {
            settings.General.Theme = ThemeMode.Dark;
            settings.General.AccentColorHex = "#7C3AED";
            settings.General.FontScale = 1.15;
            settings.Processing.MaxConcurrentJobs = 3;
        });
        Assert.True(File.Exists(paths.SettingsFile));

        var second = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await second.LoadAsync();
        Assert.Equal(ThemeMode.Dark, second.Settings.General.Theme);
        Assert.Equal("#7C3AED", second.Settings.General.AccentColorHex);
        Assert.Equal(1.15, second.Settings.General.FontScale, 3);
        Assert.Equal(3, second.Settings.Processing.MaxConcurrentJobs);
    }

    [Fact]
    public async Task Video_upscaler_defaults_are_persisted_and_reloaded()
    {
        using var paths = new TempAppPaths();
        var first = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await first.LoadAsync();

        await first.UpdateAsync(settings =>
        {
            settings.VideoUpscaler.EnableSuperResolution = false;
            settings.VideoUpscaler.EnableInterpolation = true;
            settings.VideoUpscaler.ImageModelId = "realesrgan-x2plus";
            settings.VideoUpscaler.InterpolationModelId = "rife-4.25-lite";
            settings.VideoUpscaler.FrameRateMultiplier = 3;
            settings.VideoUpscaler.VideoBitrateKbps = 18000;
            settings.VideoUpscaler.FileNameSuffix = "_enhanced";
            settings.VideoUpscaler.IncludeSubfolders = false;
            settings.VideoUpscaler.CollisionPolicy = CollisionPolicy.Overwrite;
            settings.VideoUpscaler.DefaultOutputDirectory = Path.Combine(paths.DefaultOutputDirectory, "Video Defaults");
        });

        var second = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await second.LoadAsync();
        var video = second.Settings.VideoUpscaler;
        Assert.False(video.EnableSuperResolution);
        Assert.True(video.EnableInterpolation);
        Assert.Equal("realesrgan-x2plus", video.ImageModelId);
        Assert.Equal("rife-4.25-lite", video.InterpolationModelId);
        Assert.Equal(3, video.FrameRateMultiplier);
        Assert.Equal(18000, video.VideoBitrateKbps);
        Assert.Equal("_enhanced", video.FileNameSuffix);
        Assert.False(video.IncludeSubfolders);
        Assert.Equal(CollisionPolicy.Overwrite, video.CollisionPolicy);
        Assert.Equal(Path.Combine(paths.DefaultOutputDirectory, "Video Defaults"), video.DefaultOutputDirectory);
    }

    [Fact]
    public async Task Ocr_defaults_and_per_format_destinations_round_trip()
    {
        using var paths = new TempAppPaths();
        var first = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await first.LoadAsync();

        var pngFolder = Path.Combine(paths.DefaultOutputDirectory, "png-only");
        var jpgFolder = Path.Combine(paths.DefaultOutputDirectory, "jpg-only");
        await first.UpdateAsync(settings =>
        {
            settings.Ocr.ModelId = "ppocrv6-tiny";
            settings.Ocr.ExecutionProvider = ExecutionProviderKind.DirectML;
            settings.Ocr.GeneratePdf = true;
            settings.Ocr.Preprocess = false;
            settings.Ocr.DefaultOutputDirectory = Path.Combine(paths.DefaultOutputDirectory, "ocr-default");
            settings.Converter.FormatOutputDirectories["png"] = pngFolder;
            settings.Converter.FormatOutputDirectories["JPG"] = jpgFolder;
        });

        var second = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await second.LoadAsync();
        Assert.Equal("ppocrv6-tiny", second.Settings.Ocr.ModelId);
        Assert.Equal(ExecutionProviderKind.DirectML, second.Settings.Ocr.ExecutionProvider);
        Assert.True(second.Settings.Ocr.GeneratePdf);
        Assert.False(second.Settings.Ocr.Preprocess);
        Assert.Equal(Path.Combine(paths.DefaultOutputDirectory, "ocr-default"),
            second.Settings.Ocr.DefaultOutputDirectory);
        Assert.Equal(pngFolder, second.Settings.Converter.FormatOutputDirectories["PNG"]);
        Assert.Equal(jpgFolder, second.Settings.Converter.FormatOutputDirectories["jpg"]);
    }

    [Fact]
    public void Ocr_settings_round_trip_including_ordered_pages()
    {
        var serializer = new OperationSettingsSerializer();
        var settings = new OcrSettings
        {
            OcrModelId = "ppocrv6-tiny",
            GeneratePdf = true,
            DocumentName = "Scanned Chapter 1",
            Preprocess = false,
            OutputDirectory = @"C:\output\ocr",
            CollisionPolicy = CollisionPolicy.Skip,
            PagePaths = [@"C:\pages\02.png", @"C:\pages\01.png"]
        };

        var json = serializer.Serialize(settings);
        var loaded = Assert.IsType<OcrSettings>(
            serializer.Deserialize(OperationKind.OcrDocument, json));

        Assert.Equal("ppocrv6-tiny", loaded.OcrModelId);
        Assert.True(loaded.GeneratePdf);
        Assert.Equal("Scanned Chapter 1", loaded.DocumentName);
        Assert.False(loaded.Preprocess);
        Assert.Equal(@"C:\output\ocr", loaded.OutputDirectory);
        Assert.Equal(CollisionPolicy.Skip, loaded.CollisionPolicy);
        Assert.Equal([@"C:\pages\02.png", @"C:\pages\01.png"], loaded.PagePaths);
    }

    [Fact]
    public async Task History_round_trips_search_filter_and_clear()
    {
        using var paths = new TempAppPaths();
        var database = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
        var repository = new HistoryRepository(database, NullLogger<HistoryRepository>.Instance);
        await repository.InitializeAsync();

        var createdAt = DateTimeOffset.Now;
        var id = await repository.AddAsync(new HistoryEntry
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.UpscaleAi,
            Title = "AI upscale",
            InputPath = @"C:\media\alpha.png",
            OutputPath = @"C:\media\alpha_4x.png",
            InputFileName = "alpha.png",
            OutputFileName = "alpha_4x.png",
            InputBytes = 1024,
            OutputBytes = 8192,
            FormatId = "png",
            SettingsJson = """{"Method":"Ai","ScaleFactor":4}""",
            DurationMs = 1234,
            Status = JobItemState.Completed,
            CreatedAt = createdAt,
            VerificationSummary = "PNG · 4× · verified"
        });
        await repository.AddAsync(new HistoryEntry
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = OperationKind.AudioConvert,
            Title = "Audio conversion",
            InputPath = @"C:\media\tone.wav",
            OutputPath = @"C:\media\tone.mp3",
            InputFileName = "tone.wav",
            OutputFileName = "tone.mp3",
            InputBytes = 2048,
            OutputBytes = 4096,
            FormatId = "mp3",
            DurationMs = 500,
            Status = JobItemState.Completed,
            CreatedAt = createdAt.AddMinutes(-1)
        });

        Assert.True(id > 0);
        Assert.Equal(2, await repository.CountAsync());
        var all = await repository.GetRecentAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("alpha_4x.png", all[0].OutputFileName);

        var search = await repository.GetRecentAsync(search: "alpha");
        Assert.Single(search);
        Assert.Equal(OperationKind.UpscaleAi, search[0].Operation);
        Assert.Equal(8192, search[0].OutputBytes);

        var filtered = await repository.GetRecentAsync(operation: OperationKind.AudioConvert);
        Assert.Single(filtered);
        Assert.Equal("tone.mp3", filtered[0].OutputFileName);

        await repository.ClearAsync();
        Assert.Equal(0, await repository.CountAsync());
    }

    [Fact]
    public async Task Job_repository_round_trips_batch_and_items()
    {
        using var paths = new TempAppPaths();
        var database = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
        var serializer = new OperationSettingsSerializer();
        var repository = new JobRepository(database, serializer, NullLogger<JobRepository>.Instance);
        await repository.InitializeAsync();

        var job = new ProcessingJob
        {
            Operation = OperationKind.ImageConvert,
            Title = "Batch conversion",
            State = JobState.Completed,
            OutputDirectory = paths.DefaultOutputDirectory,
            Settings = new ImageConversionSettings { OutputFormatId = "webp", OutputDirectory = paths.DefaultOutputDirectory },
            BatchSummary = "1 completed"
        };
        job.Items.Add(new JobItem
        {
            JobId = job.Id,
            InputPath = @"C:\media\one.png",
            FileName = "one.png",
            OutputPath = Path.Combine(paths.DefaultOutputDirectory, "one.webp"),
            State = JobItemState.Completed,
            Progress = 1,
            InputBytes = 100,
            OutputBytes = 200,
            OutputFormatId = "webp",
            VerificationSummary = "WebP · 100 × 100",
            CompletedAt = DateTimeOffset.Now
        });

        await repository.SaveAsync(job);
        var loaded = await repository.GetAsync(job.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Batch conversion", loaded!.Title);
        Assert.Equal(JobState.Completed, loaded.State);
        Assert.Single(loaded.Items);
        Assert.Equal("one.png", loaded.Items[0].FileName);
        Assert.Equal(JobItemState.Completed, loaded.Items[0].State);
        Assert.IsType<ImageConversionSettings>(loaded.Settings);
        Assert.Equal("webp", ((ImageConversionSettings)loaded.Settings).OutputFormatId);

        var recent = await repository.GetRecentAsync();
        Assert.Single(recent);
        await repository.DeleteAsync(job.Id);
        Assert.Null(await repository.GetAsync(job.Id));
    }
}
