using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PixelForge.Application.Capabilities;
using PixelForge.Application.Jobs;
using PixelForge.Application.Media;
using PixelForge.Application.Models;
using PixelForge.Application.Pipelines;
using PixelForge.Application.Updates;
using PixelForge.Domain.Interfaces;
using PixelForge.Engines.FFmpeg;
using PixelForge.Engines.Images;
using PixelForge.Engines.Documents;
using PixelForge.Engines.Archives;
using PixelForge.Engines.Upscaling;
using PixelForge.Engines.ImageTools;
using PixelForge.Engines.FFmpegTools;
using PixelForge.Engines.OCR;
using PixelForge.Infrastructure.Diagnostics;
using PixelForge.Infrastructure.FileSystem;
using PixelForge.Infrastructure.Logging;
using PixelForge.Infrastructure.Paths;
using PixelForge.Infrastructure.Persistence;
using PixelForge.Infrastructure.Pipelines;
using PixelForge.Infrastructure.ProcessExecution;
using PixelForge.Infrastructure.Settings;
using PixelForge.Infrastructure.Watch;

namespace PixelForge.Composition;

public static class ServiceRegistration
{
    public static IServiceCollection AddPixelForge(this IServiceCollection services)
    {
        var paths = new AppPaths();
        paths.EnsureCreated();
        var fileLogger = new FileLogger(paths.LogsDirectory);

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(fileLogger);
        });

        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton(fileLogger);
        services.AddSingleton<IAppLogger>(fileLogger);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IOperationSettingsSerializer, OperationSettingsSerializer>();
        services.AddSingleton<IFileNameService, FileNameService>();
        services.AddSingleton<IOutputPathService, OutputPathService>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IJobRepository, JobRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();
        services.AddSingleton<IPipelineStore, PipelineFileStore>();

        services.AddSingleton<ImageEngine>();
        services.AddSingleton<FFmpegEngine>();
        services.AddSingleton<RifeInterpolator>();
        services.AddSingleton<UpscalingEngine>();
        services.AddSingleton<DocumentEngine>();
        services.AddSingleton<ArchiveEngine>();
        services.AddSingleton<ImageToolsEngine>();
        services.AddSingleton<FFmpegToolsEngine>();
        services.AddSingleton<VideoUpscalingEngine>();
        services.AddSingleton<OcrEngine>();
        services.AddSingleton<IProcessingEngine>(sp => sp.GetRequiredService<ImageEngine>());
        services.AddSingleton<IProcessingEngine>(sp => sp.GetRequiredService<FFmpegEngine>());
        services.AddSingleton<IProcessingEngine>(sp => sp.GetRequiredService<UpscalingEngine>());
        services.AddSingleton<IProcessingEngine>(sp => sp.GetRequiredService<DocumentEngine>());
        services.AddSingleton<IProcessingEngine>(sp => sp.GetRequiredService<ArchiveEngine>());
        services.AddSingleton<IProcessingEngine>(sp => sp.GetRequiredService<ImageToolsEngine>());
        services.AddSingleton<IProcessingEngine>(sp => sp.GetRequiredService<FFmpegToolsEngine>());
        services.AddSingleton<IProcessingEngine>(sp => sp.GetRequiredService<VideoUpscalingEngine>());
        services.AddSingleton<IProcessingEngine>(sp => sp.GetRequiredService<OcrEngine>());
        services.AddSingleton<IOcrReviewService>(sp => sp.GetRequiredService<OcrEngine>());
        services.AddSingleton<IFrameUpscaler>(sp => sp.GetRequiredService<UpscalingEngine>());
        services.AddSingleton<IFrameInterpolator>(sp => sp.GetRequiredService<RifeInterpolator>());
        services.AddSingleton<IModelValidator>(sp => sp.GetRequiredService<UpscalingEngine>());

        services.AddSingleton<ICapabilityRegistry, CapabilityRegistry>();
        services.AddSingleton<IMediaInspector, MediaInspector>();
        services.AddSingleton<IFolderScanner, FolderScanner>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<IWatchFolderService, WatchFolderService>();
        services.AddSingleton<INcnnRuntimeManager, NcnnRuntimeManager>();
        services.AddSingleton<ILlamaCppRuntimeManager, LlamaCppRuntimeManager>();
        services.AddSingleton<LlamaCppRuntimeUpgradeService>();
        services.AddSingleton<ILlamaCppOcrService, LlamaCppOcrService>();
        services.AddSingleton<IModelManager, ModelManager>();
        services.AddSingleton<IOcrModelManager, OcrModelManager>();
        services.AddSingleton<JobValidator>();
        services.AddSingleton<IJobService, JobService>();
        services.AddSingleton<PipelineValidator>();
        services.AddSingleton<IPipelineRunService, PipelineRunService>();
        services.AddSingleton<IDiagnosticsService, SystemDiagnosticsService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        return services;
    }

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var paths = services.GetRequiredService<IAppPaths>();
        paths.EnsureCreated();
        var logger = services.GetRequiredService<FileLogger>();
        logger.Initialize(paths.LogsDirectory, "Information");

        var settings = services.GetRequiredService<ISettingsService>();
        await settings.LoadAsync(cancellationToken);
        logger.SetMinimumLevel(settings.Settings.Processing.LogLevel);

        await services.GetRequiredService<INcnnRuntimeManager>().InitializeAsync(cancellationToken);
        await services.GetRequiredService<ILlamaCppRuntimeManager>().InitializeAsync(cancellationToken);

        var registry = services.GetRequiredService<ICapabilityRegistry>();
        await registry.InitializeAsync(cancellationToken);

        var models = services.GetRequiredService<IModelManager>();
        await models.InitializeAsync(cancellationToken);

        var watch = services.GetRequiredService<IWatchFolderService>();
        await watch.InitializeAsync(cancellationToken);
    }
}
