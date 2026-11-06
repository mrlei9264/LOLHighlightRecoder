using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using LolHighlight.Recording.Infrastructure;

namespace LolHighlight.Desktop;

public partial class App : Application
{
    private static ServiceProvider? _serviceProvider;
    private Window? window;
    private Services.EventNotificationService? _notifications;
    internal Window? MainWindow => window;
    internal bool PreviewEventNotification(string style) => _notifications?.Preview(style) == true;
    internal string EventNotificationPreviewError => _notifications?.LastError ?? "事件提示服务尚未就绪，请重试。";

    public App() => InitializeComponent();

    public static T GetService<T>() where T : class
    {
        return _serviceProvider?.GetService<T>()
            ?? throw new InvalidOperationException("Service not configured");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _serviceProvider = DependencyInjection.Configure();

            window = new MainWindow();
            _notifications = new Services.EventNotificationService(GetService<LolHighlight.Recording.Services.IRecordingSessionService>(),
                window.DispatcherQueue, GetService<LolHighlight.Recording.Models.AppConfig>());
            window.Closed += (_, _) =>
            {
                _notifications?.Dispose();
                _notifications = null;
                _serviceProvider?.Dispose();
                _serviceProvider = null;
                window = null;
            };
            window.Activate();
        }
        catch (Exception exception)
        {
            var runtime = new AppPaths().ResolveRuntimeDirectory(".runtime");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "ui-crash.log"), exception.ToString());
            throw;
        }
    }
}
