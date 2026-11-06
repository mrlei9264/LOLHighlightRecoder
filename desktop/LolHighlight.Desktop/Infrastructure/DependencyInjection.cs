using Microsoft.Extensions.DependencyInjection;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;
using LolHighlight.Desktop.ViewModels;

namespace LolHighlight.Desktop.Infrastructure;

public static class DependencyInjection
{
    public static ServiceProvider Configure()
    {
        var services = new ServiceCollection();

        var paths = new AppPaths();
        var config = LoadConfig(paths.ConfigPath);
        paths.NormalizeDataDirectories(config);

        services.AddSingleton(paths);
        services.AddSingleton(config);
        services.AddSingleton<MatchReviewStore>();

        // Services with parameterless constructors
        services.AddSingleton<IRiotApiClient, RiotApiClient>();
        services.AddSingleton<IHighlightEvaluator, HighlightEvaluator>();
        services.AddSingleton<IVideoScanner, VideoScanner>();
        services.AddSingleton<IMetadataFooterService, MetadataFooterService>();
        services.AddSingleton<IGameAudioCapture, GameAudioCapture>();
        services.AddSingleton<IMicrophoneCapture, MicrophoneCapture>();
        services.AddSingleton<IRecordingClock, RecordingClock>();
        services.AddSingleton<RecordingPreviewTap>();
        services.AddSingleton<IGameWindowLocator, GameWindowLocator>();
        services.AddSingleton<IStorageQuotaManager, StorageQuotaManager>();
        services.AddSingleton<IColorGradingService>(sp => new ColorGradingService(config, paths,
            sp.GetRequiredService<IMetadataFooterService>(), () => sp.GetRequiredService<IRecordingSessionService>().IsRunning));
        services.AddSingleton<LolHighlight.Desktop.Services.IColorPickerService>(_ =>
            new LolHighlight.Desktop.Services.ColorPickerService(() => (Microsoft.UI.Xaml.Application.Current as App)?.MainWindow));
        services.AddSingleton<GradingViewModel>();
        services.AddSingleton<LolHighlight.Desktop.Services.VideoThumbnailService>();

        services.AddSingleton<ISettingsService>(sp =>
            new SettingsService(paths.ConfigPath, config, () => sp.GetRequiredService<IRecordingSessionService>().IsRunning, paths));
        services.AddSingleton<LolHighlight.Desktop.Services.ISettingsPickerService>(_ =>
            new LolHighlight.Desktop.Services.SettingsPickerService(() => (Microsoft.UI.Xaml.Application.Current as App)?.MainWindow));

        services.AddSingleton<IMemoryRingBuffer>(sp =>
            new MemoryRingBuffer(
                config.RetentionSeconds,
                config.MemoryBufferMb * 1024L * 1024));

        services.AddSingleton<IFfmpegPipeline>(sp =>
        {
            var ringBuffer = sp.GetRequiredService<IMemoryRingBuffer>();
            var clock = sp.GetRequiredService<IRecordingClock>();
            return new FfmpegPipeline(ringBuffer, config, clock, sp.GetRequiredService<RecordingPreviewTap>());
        });

        services.AddSingleton<IExportManager>(sp =>
        {
            var ringBuffer = sp.GetRequiredService<IMemoryRingBuffer>();
            var footer = sp.GetRequiredService<IMetadataFooterService>();
            var quota = sp.GetRequiredService<IStorageQuotaManager>();
            return new ExportManager(ringBuffer, footer, quota, config);
        });

        services.AddSingleton<IClipCoordinator>(sp =>
        {
            var export = sp.GetRequiredService<IExportManager>();
            var clock = sp.GetRequiredService<IRecordingClock>();
            return new ClipCoordinator(config, export, clock, paths, sp.GetRequiredService<ITelemetryCollector>(), sp.GetRequiredService<MatchReviewStore>());
        });

        services.AddSingleton<ITelemetryCollector>(sp =>
            new TelemetryCollector(new PlayerIdentity("unknown", "unknown", "unknown")));

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<IRecordingSessionService, RecordingSessionService>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static AppConfig LoadConfig(string configPath)
    {
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                config.Validate();
                return config;
            }
            catch (Exception error)
            {
                RuntimeDiagnostics.Write(Path.Combine(Path.GetDirectoryName(configPath)!, ".runtime"),
                    $"配置读取失败，暂用默认值；原文件保持不变：{error.Message}");
            }
        }
        return new AppConfig();
    }
}
