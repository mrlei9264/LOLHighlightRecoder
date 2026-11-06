using System.Globalization;
using LolHighlight.Desktop.Services;
using LolHighlight.Desktop.ViewModels;
using LolHighlight.Recording.Services;
using Moq;

namespace LolHighlight.Desktop.Tests;

public sealed class SettingsInteractionTests
{
    [Fact]
    public async Task SelectedFolder_IsOnlySavedAfterExplicitSave()
    {
        var config = new AppConfig();
        var settings = new SavedSettings(config);
        var picker = new Mock<ISettingsPickerService>();
        picker.Setup(p => p.PickOutputFolderAsync()).ReturnsAsync(@"D:\Clips\我的高光");
        using var vm = new SettingsViewModel(settings, config, pickers: picker.Object);
        await ((AsyncRelayCommand)vm.SelectOutputFolderCommand).ExecuteAsync(null);
        Assert.Equal(@"D:\Clips\我的高光", vm.OutputDir);
        Assert.Equal("recordings", config.OutputDir);
        Assert.Null(settings.Saved);
        Assert.True(vm.HasUnsavedChanges);
        vm.SaveCommand.Execute(null);
        Assert.Equal(vm.OutputDir, settings.Saved!.OutputDir);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task CancellingFolderPicker_PreservesSettingsAndCleanState()
    {
        var config = new AppConfig();
        var picker = new Mock<ISettingsPickerService>();
        picker.Setup(p => p.PickOutputFolderAsync()).ReturnsAsync((string?)null);
        using var vm = new SettingsViewModel(new SavedSettings(config), config, pickers: picker.Object);
        await ((AsyncRelayCommand)vm.SelectOutputFolderCommand).ExecuteAsync(null);
        Assert.Equal("recordings", vm.OutputDir);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.IsPickingPath);
        Assert.False(vm.SaveFailed);
    }

    [Fact]
    public async Task PickerFailure_IsShownAndDoesNotLeaveFormLocked()
    {
        var config = new AppConfig();
        var picker = new Mock<ISettingsPickerService>();
        picker.Setup(p => p.PickOutputFolderAsync()).ThrowsAsync(new IOException("picker unavailable"));
        using var vm = new SettingsViewModel(new SavedSettings(config), config, pickers: picker.Object);
        await ((AsyncRelayCommand)vm.SelectOutputFolderCommand).ExecuteAsync(null);
        Assert.True(vm.SaveFailed);
        Assert.Contains("picker unavailable", vm.SaveMessage);
        Assert.False(vm.IsPickingPath);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task PathPickers_CannotOpenTwoDialogsAtOnce()
    {
        var config = new AppConfig();
        var selected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new Mock<ISettingsPickerService>();
        picker.Setup(p => p.PickOutputFolderAsync()).Returns(selected.Task);
        using var vm = new SettingsViewModel(new SavedSettings(config), config, pickers: picker.Object);
        var first = ((AsyncRelayCommand)vm.SelectOutputFolderCommand).ExecuteAsync(null);
        Assert.True(vm.IsPickingPath);
        await ((AsyncRelayCommand)vm.SelectFfmpegCommand).ExecuteAsync(null);
        picker.Verify(p => p.PickFfmpegExecutableAsync(), Times.Never);
        selected.SetResult(null);
        await first;
        Assert.True(vm.CanEditPaths);
    }

    [Theory]
    [InlineData("8000k", 8d)]
    [InlineData("8000000", 8d)]
    [InlineData("12.5M", 12.5d)]
    public void ExistingBitrateUnits_LoadWithoutRewritingConfiguration(string rate, double mbps)
    {
        var config = new AppConfig { Bitrate = rate };
        using var vm = new SettingsViewModel(new SavedSettings(config), config);
        Assert.Equal(mbps, vm.VideoBitrateMbps);
        vm.VideoBitrateMbps = mbps; // Same-value pushback from a TwoWay numeric binding.
        Assert.Equal(rate, vm.Bitrate);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void NumericBitrate_SavesInvariantUnitsAndRaisesMaximumWhenNeeded()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var config = new AppConfig();
            var settings = new SavedSettings(config);
            using var vm = new SettingsViewModel(settings, config);
            vm.VideoBitrateMbps = 30.5;
            vm.SaveCommand.Execute(null);
            Assert.Equal("30.5M", settings.Saved!.Bitrate);
            Assert.Equal("30.5M", settings.Saved.MaxBitrate);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void BitrateSliders_SaveEachEncodingRateInItsOriginalSetting()
    {
        var config = new AppConfig();
        var settings = new SavedSettings(config);
        using var vm = new SettingsViewModel(settings, config);
        vm.VideoBitrateMbps = 40;
        vm.MaxBitrateMbps = 50;
        vm.RateBufferMegabits = 80;
        vm.SaveCommand.Execute(null);
        Assert.Equal("40M", settings.Saved!.Bitrate);
        Assert.Equal("50M", settings.Saved.MaxBitrate);
        Assert.Equal("80M", settings.Saved.BufferSize);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void DurationAndTelemetrySliders_SaveSecondsAndMillisecondsCorrectly()
    {
        var config = new AppConfig();
        var settings = new SavedSettings(config);
        using var vm = new SettingsViewModel(settings, config);
        vm.PreRoll = 30;
        vm.PostRoll = 10;
        vm.SegmentGrace = 3;
        vm.TelemetryIntervalMilliseconds = 250;
        vm.SaveCommand.Execute(null);
        Assert.Equal(30, settings.Saved!.PreRollSeconds);
        Assert.Equal(10, settings.Saved.PostRollSeconds);
        Assert.Equal(3, settings.Saved.SegmentGraceSeconds);
        Assert.Equal(0.25, settings.Saved.TelemetryIntervalSeconds);
        Assert.Equal(0.25, settings.Saved.PollIntervalSeconds);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void ExistingCustomFpsAndMemoryRemainUnchangedInSliderBindings()
    {
        var config = new AppConfig { Fps = 75, MemoryBufferMb = 768 };
        using var vm = new SettingsViewModel(new SavedSettings(config), config);
        vm.FpsValue = vm.FpsValue;
        vm.MemoryBufferValue = vm.MemoryBufferValue;
        Assert.Equal(75, vm.Fps);
        Assert.Equal(768, vm.MemoryBufferMb);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void TrackModeOnlyEnablesForTwoSources_AndPreservesPreference()
    {
        var config = new AppConfig { AudioEnabled = false, MicrophoneEnabled = true };
        using var vm = new SettingsViewModel(new SavedSettings(config), config);
        Assert.True(vm.MicrophoneEnabled);
        Assert.False(vm.CanSeparateAudioTracks);
        vm.AudioEnabled = true;
        Assert.True(vm.CanSeparateAudioTracks);
        vm.MicrophoneEnabled = false;
        Assert.False(vm.CanSeparateAudioTracks);
        Assert.True(vm.SeparateAudioTracks);
    }

    [Fact]
    public void ReturningToSavedValuesClearsDirtyState()
    {
        var config = new AppConfig();
        using var vm = new SettingsViewModel(new SavedSettings(config), config);
        vm.Fps = 30;
        Assert.True(vm.CanSave);
        vm.Fps = 60;
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task CustomExecutableSelection_AndBundledModeCanBeSwitchedWithoutLosingChoice()
    {
        var config = new AppConfig();
        var picker = new Mock<ISettingsPickerService>();
        picker.Setup(p => p.PickFfmpegExecutableAsync()).ReturnsAsync(@"D:\Tools\ffmpeg.exe");
        using var vm = new SettingsViewModel(new SavedSettings(config), config, pickers: picker.Object);
        vm.UseCustomFfmpeg = true;
        Assert.False(vm.CanSave);
        await ((AsyncRelayCommand)vm.SelectFfmpegCommand).ExecuteAsync(null);
        Assert.Equal(@"D:\Tools\ffmpeg.exe", vm.FfmpegPath);
        Assert.True(vm.CanSave);
        vm.UseCustomFfmpeg = false;
        Assert.Equal("ffmpeg", vm.FfmpegPath);
        Assert.False(vm.HasUnsavedChanges);
        vm.UseCustomFfmpeg = true;
        Assert.Equal(@"D:\Tools\ffmpeg.exe", vm.FfmpegPath);
    }

    [Fact]
    public void SaveAvailabilityFollowsListeningState()
    {
        var config = new AppConfig();
        var listening = false;
        var session = new Mock<IRecordingSessionService>();
        session.SetupGet(s => s.IsRunning).Returns(() => listening);
        using var vm = new SettingsViewModel(new SavedSettings(config), config, session: session.Object);
        vm.PreRoll = 30;
        Assert.True(vm.CanSave);
        listening = true;
        session.Raise(s => s.StatusChanged += null, new RecordingStatusChangedEventArgs(
            new(RecordingStatus.WaitingForGame, "", "", 0, null, null)));
        Assert.False(vm.CanSave);
        Assert.Contains("停止后", vm.SettingsStateText);
        listening = false;
        session.Raise(s => s.StatusChanged += null, new RecordingStatusChangedEventArgs(
            new(RecordingStatus.Idle, "", "", 0, null, null)));
        Assert.True(vm.CanSave);
    }

    private sealed class SavedSettings(AppConfig config) : ISettingsService
    {
        public AppConfig? Saved { get; private set; }
        public AppConfig Load() => config.Clone();
        public void Save(AppConfig settings)
        {
            settings.Validate();
            Saved = settings.Clone();
        }
    }
}
