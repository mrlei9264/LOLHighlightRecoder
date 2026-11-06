using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Services;
using LolHighlight.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LolHighlight.Desktop.Tests;

public sealed class DiContainerTests
{
    [Fact]
    public void Configure_ReturnsServiceProvider()
    {
        using var provider = DependencyInjection.Configure();

        Assert.NotNull(provider);
    }

    [Fact]
    public void Resolve_AppConfig_ReturnsDefault()
    {
        using var provider = DependencyInjection.Configure();
        var config = provider.GetRequiredService<AppConfig>();

        Assert.NotNull(config);
        Assert.True(config.Fps > 0);
    }

    [Fact]
    public void Resolve_AllServices_ReturnsNonNull()
    {
        using var provider = DependencyInjection.Configure();

        Assert.NotNull(provider.GetRequiredService<IRiotApiClient>());
        Assert.NotNull(provider.GetRequiredService<IHighlightEvaluator>());
        Assert.NotNull(provider.GetRequiredService<IVideoScanner>());
        Assert.NotNull(provider.GetRequiredService<IMetadataFooterService>());
        Assert.NotNull(provider.GetRequiredService<IGameAudioCapture>());
        Assert.NotNull(provider.GetRequiredService<IMicrophoneCapture>());
        Assert.NotNull(provider.GetRequiredService<IRecordingClock>());
        Assert.NotNull(provider.GetRequiredService<IStorageQuotaManager>());
        Assert.NotNull(provider.GetRequiredService<ISettingsService>());
        Assert.NotNull(provider.GetRequiredService<IMemoryRingBuffer>());
        Assert.NotNull(provider.GetRequiredService<IFfmpegPipeline>());
        Assert.NotNull(provider.GetRequiredService<IExportManager>());
        Assert.NotNull(provider.GetRequiredService<IClipCoordinator>());
        Assert.NotNull(provider.GetRequiredService<ITelemetryCollector>());
    }

    [Fact]
    public void Resolve_ViewModels_ReturnsNonNull()
    {
        using var provider = DependencyInjection.Configure();

        Assert.NotNull(provider.GetRequiredService<MainViewModel>());
        Assert.NotNull(provider.GetRequiredService<HomeViewModel>());
        Assert.NotNull(provider.GetRequiredService<LibraryViewModel>());
        Assert.NotNull(provider.GetRequiredService<SettingsViewModel>());
    }
}
