using System.ComponentModel;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;
using LolHighlight.Desktop.ViewModels;

namespace LolHighlight.Desktop.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void Constructor_LoadsFromSettingsService()
    {
        var config = new AppConfig
        {
            CaptureMode = "display",
            Encoder = "nvenc",
            Fps = 30,
            Bitrate = "8M",
            PreRollSeconds = 30,
            PostRollSeconds = 10,
            DiskQuotaGb = 100,
            MinimumFreeGb = 10,
            AudioEnabled = false,
            MicrophoneEnabled = true,
            SeparateAudioTracks = false
        };
        var vm = new SettingsViewModel(new FakeSettingsService(config), config);

        Assert.Equal("display", vm.CaptureMode);
        Assert.Equal("nvenc", vm.Encoder);
        Assert.Equal(30, vm.Fps);
        Assert.Equal("8M", vm.Bitrate);
        Assert.Equal(30, vm.PreRoll);
        Assert.Equal(10, vm.PostRoll);
        Assert.Equal(100, vm.DiskQuotaGb);
        Assert.Equal(10, vm.MinimumFreeGb);
        Assert.False(vm.AudioEnabled);
        Assert.True(vm.MicrophoneEnabled);
        Assert.False(vm.SeparateAudioTracks);
    }

    [Fact]
    public void SaveCommand_SavesToSettingsService()
    {
        var config = new AppConfig();
        var service = new FakeSettingsService(config);
        var vm = new SettingsViewModel(service, config);

        vm.CaptureMode = "display";
        vm.Encoder = "amd";
        vm.Fps = 120;
        vm.Bitrate = "30M";
        vm.PreRoll = 60;
        vm.PostRoll = 20;
        vm.DiskQuotaGb = 200;
        vm.MinimumFreeGb = 20;
        vm.AudioEnabled = false;
        vm.MicrophoneEnabled = true;
        vm.SeparateAudioTracks = false;

        vm.SaveCommand.Execute(null);

        Assert.True(service.SaveCalled);
        Assert.Equal("设置已保存", vm.SaveMessage);
    }

    [Fact]
    public void SaveCommand_OnError_ShowsErrorMessage()
    {
        var config = new AppConfig();
        var service = new FakeSettingsService(config) { ShouldFail = true };
        var vm = new SettingsViewModel(service, config);

        vm.SaveCommand.Execute(null);

        Assert.Contains("保存失败", vm.SaveMessage);
    }

    [Fact]
    public void PropertyChanged_IsRaised()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(new FakeSettingsService(config), config);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.CaptureMode = "display";
        vm.Fps = 30;

        Assert.Contains("CaptureMode", changed);
        Assert.Contains("Fps", changed);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        private readonly AppConfig _config;

        public FakeSettingsService(AppConfig config) => _config = config;

        public bool SaveCalled { get; private set; }
        public bool ShouldFail { get; set; }

        public AppConfig Load() => _config;

        public void Save(AppConfig config)
        {
            if (ShouldFail)
                throw new InvalidOperationException("Save failed");
            SaveCalled = true;
        }
    }
}
