using System.ComponentModel;
using System.Windows.Input;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Services;
using LolHighlight.Desktop.ViewModels;

namespace LolHighlight.Desktop.Tests;

public sealed class HomeViewModelTests
{
    [Fact]
    public void Constructor_SetsIdleState()
    {
        var session = new FakeSession(RecordingStatus.Idle);
        var vm = new HomeViewModel(session, new FakeVideoScanner(), new AppPaths(), new AppConfig());

        Assert.Equal("未启动", vm.StatusTitle);
        Assert.True(vm.IsStartVisible);
        Assert.False(vm.IsStopVisible);
    }

    [Fact]
    public void Constructor_WhenRecording_ShowsStopButton()
    {
        var session = new FakeSession(RecordingStatus.Recording);
        var vm = new HomeViewModel(session, new FakeVideoScanner(), new AppPaths(), new AppConfig());

        Assert.True(vm.IsStopVisible);
        Assert.False(vm.IsStartVisible);
    }

    [Fact]
    public async Task StartCommand_CallsSessionStart()
    {
        var session = new FakeSession(RecordingStatus.Idle);
        var vm = new HomeViewModel(session, new FakeVideoScanner(), new AppPaths(), new AppConfig());

        await ((AsyncRelayCommand)vm.StartCommand).ExecuteAsync(null);

        Assert.True(session.StartCalled);
    }

    [Fact]
    public async Task StopCommand_CallsSessionStop()
    {
        var session = new FakeSession(RecordingStatus.Recording);
        var vm = new HomeViewModel(session, new FakeVideoScanner(), new AppPaths(), new AppConfig());

        await ((AsyncRelayCommand)vm.StopCommand).ExecuteAsync(null);

        Assert.True(session.StopCalled);
    }

    [Fact]
    public void StatusChanged_UpdatesUIProperties()
    {
        var session = new FakeSession(RecordingStatus.Idle);
        var vm = new HomeViewModel(session, new FakeVideoScanner(), new AppPaths(), new AppConfig());

        session.EmitStatus(RecordingStatus.Recording, "录制中", "正在录制", 10.5);

        Assert.Equal("录制中", vm.StatusTitle);
        Assert.Contains("10.5", vm.StatusDetail);
        Assert.True(vm.IsStopVisible);
    }

    [Fact]
    public void PropertyChanged_IsRaised()
    {
        var session = new FakeSession(RecordingStatus.Idle);
        var vm = new HomeViewModel(session, new FakeVideoScanner(), new AppPaths(), new AppConfig());

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        session.EmitStatus(RecordingStatus.Recording, "录制中", "正在录制", 5.0);

        Assert.Contains("StatusTitle", changed);
        Assert.Contains("StatusDetail", changed);
        Assert.Contains("IsStartVisible", changed);
        Assert.Contains("IsStopVisible", changed);
    }

    [Fact]
    public void LoadRecentVideos_OnConstruction()
    {
        var session = new FakeSession(RecordingStatus.Idle);
        var scanner = new FakeVideoScanner(
            new VideoItem(@"D:\videos\vid1.mp4", "vid1.mp4", "10.0s"),
            new VideoItem(@"D:\videos\vid2.mp4", "vid2.mp4", "20.0s"));
        var vm = new HomeViewModel(session, scanner, new AppPaths(), new AppConfig());

        Assert.Equal(2, vm.RecentVideos.Count);
        Assert.Equal("vid1.mp4", vm.RecentVideos[0].Name);
    }

    private sealed class FakeSession : IRecordingSessionService
    {
        private readonly RecordingStatus _initialStatus;

        public FakeSession(RecordingStatus initialStatus) => _initialStatus = initialStatus;

        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }

        public RecordingSnapshot Snapshot => new(
            _initialStatus,
            _initialStatus == RecordingStatus.Recording ? "录制中" : "未启动",
            "",
            0,
            null,
            null);

        public bool IsRunning => _initialStatus is not RecordingStatus.Idle and not RecordingStatus.Error;

        public event EventHandler<RecordingStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<HighlightDetectedEventArgs>? HighlightDetected { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalled = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCalled = true;
            return Task.CompletedTask;
        }

        public void EmitStatus(RecordingStatus status, string title, string detail, double buffered)
        {
            StatusChanged?.Invoke(this, new RecordingStatusChangedEventArgs(
                new RecordingSnapshot(status, title, detail, buffered, null, null)));
        }

        public void Dispose() { }
    }

    [Fact]
    public void DisposedViewModel_IsNotRetainedByLongLivedSession()
    {
        var session = new FakeSession(RecordingStatus.Idle);
        var reference = CreateDisposedViewModel(session);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(reference.IsAlive);
        GC.KeepAlive(session);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedViewModel(FakeSession session)
    {
        var vm = new HomeViewModel(session, new FakeVideoScanner(), new AppPaths(), new AppConfig());
        vm.Dispose();
        return new WeakReference(vm);
    }

    private sealed class FakeVideoScanner : IVideoScanner
    {
        private readonly VideoItem[] _videos;

        public FakeVideoScanner(params VideoItem[] videos) => _videos = videos;

        public List<VideoItem> ScanVideos(string directory) => _videos.ToList();
    }
}
