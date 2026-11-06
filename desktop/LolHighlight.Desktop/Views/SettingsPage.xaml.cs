using Microsoft.UI.Xaml.Controls;
using LolHighlight.Desktop.ViewModels;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;

namespace LolHighlight.Desktop.Views;

public sealed partial class SettingsPage : UserControl
{
    private readonly SettingsViewModel _vm;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _toastTimer;
    public SettingsPage()
    {
        InitializeComponent();
        _vm = App.GetService<SettingsViewModel>();
        DataContext = _vm;
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => FlushAutoSave();
        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(3);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => SaveToast.IsOpen = false;
        SaveToast.Closed += (_, _) => _toastTimer.Stop();
        _vm.PropertyChanged += SettingsChanged;
        Unloaded += (_, _) => { _saveTimer.Stop(); _toastTimer.Stop(); SaveToast.IsOpen = false; };
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.HasUnsavedChanges) or nameof(SettingsViewModel.CanSave))
        { _saveTimer.Stop(); if (_vm.CanSave) _saveTimer.Start(); }
        if (e.PropertyName is nameof(SettingsViewModel.SaveMessage) or nameof(SettingsViewModel.HasSaveMessage))
        {
            _toastTimer.Stop();
            SaveToast.IsOpen = _vm.HasSaveMessage;
            if (_vm.HasSaveMessage && !_vm.SaveFailed) _toastTimer.Start();
        }
    }
    public bool FlushAutoSave()
    {
        _saveTimer.Stop();
        if (_vm.CanSave) _vm.SaveCommand.Execute(null);
        return !_vm.HasUnsavedChanges;
    }
    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Category0 == null) return;
        var groups = new[] { Category0, Category1, Category2, Category3, Category4, Category5 };
        for (var i = 0; i < groups.Length; i++) groups[i].Visibility = Categories.SelectedIndex == i ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void PreviewNotification_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        if (DataContext is SettingsViewModel settings)
        {
            var app = Microsoft.UI.Xaml.Application.Current as App;
            NotificationPreviewFeedback.IsOpen = app?.PreviewEventNotification(settings.EventNotificationStyle) != true;
            if (NotificationPreviewFeedback.IsOpen)
                NotificationPreviewFeedback.Message = $"{app?.EventNotificationPreviewError} 可以再次点击预览重试。";
        }
    }
}
