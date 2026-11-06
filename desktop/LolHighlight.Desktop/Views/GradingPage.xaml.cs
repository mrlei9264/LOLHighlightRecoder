using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using LolHighlight.Desktop.ViewModels;

namespace LolHighlight.Desktop.Views;

public sealed partial class GradingPage : UserControl
{
    private readonly GradingViewModel _vm;
    private bool _active;
    private bool _subscribed;
    public event Action<bool>? PlaybackChanged;
    public GradingPage()
    {
        InitializeComponent(); _vm = App.GetService<GradingViewModel>(); DataContext = _vm;
        PreviewPlayer.SetGrade(_vm.SelectedTone, _vm.Strength);
        PreviewPlayer.PlaybackFailed += message => _vm.ShowError("预览失败：" + message);
        PreviewPlayer.PlaybackChanged += playing => PlaybackChanged?.Invoke(playing);
        _vm.PropertyChanged += Changed; _subscribed = true;
        Loaded += (_, _) => { if (!_subscribed) { _vm.PropertyChanged += Changed; _subscribed = true; } if (_active) UpdatePreview(); UpdateGenerate(); };
        Unloaded += (_, _) => { _vm.PropertyChanged -= Changed; _subscribed = false; PreviewPlayer.Release(); };
        UpdateGenerate();
    }
    public GradingViewModel ViewModel => _vm;
    public void SetActive(bool active) { _active = active; UpdatePreview(); if (active) _ = _vm.RefreshAsync(); }
    public async Task StopAsync() { await _vm.StopAsync(); PreviewPlayer.Release(); }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GradingViewModel.PreviewPath)) UpdatePreview();
        if (e.PropertyName is nameof(GradingViewModel.SelectedTone) or nameof(GradingViewModel.Strength) or nameof(GradingViewModel.CompareOriginal))
            PreviewPlayer.SetGrade(_vm.CompareOriginal ? null : _vm.SelectedTone, _vm.Strength);
        if (e.PropertyName is nameof(GradingViewModel.Progress) or nameof(GradingViewModel.IsBusy)) UpdateGenerate();
    }
    private void UpdateGenerate()
    {
        var fill = Windows.UI.Color.FromArgb(255, 32, 131, 137);
        if (!_vm.IsBusy) { GenerateButton.Background = new SolidColorBrush(fill); return; }
        var position = Math.Clamp(_vm.Progress / 100, 0, 1);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        brush.GradientStops.Add(new GradientStop { Color = fill, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = fill, Offset = position });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 44, 49, 52), Offset = position });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 44, 49, 52), Offset = 1 });
        GenerateButton.Background = brush;
    }
    private void UpdatePreview()
    {
        var wasPlaying = PreviewPlayer.IsPlaying;
        var path = _vm.PreviewPath;
        EmptyPreview.Visibility = !_active || !File.Exists(path) ? Visibility.Visible : Visibility.Collapsed;
        if (_active) PreviewPlayer.Open(path, wasPlaying); else PreviewPlayer.Release();
    }
}
