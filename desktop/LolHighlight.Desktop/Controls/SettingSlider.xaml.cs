using System.Globalization;
using LolHighlight.Desktop.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace LolHighlight.Desktop.Controls;

public sealed partial class SettingSlider : UserControl
{
    private bool _updating;
    private string? _parsedStops;
    private double[] _stops = [];
    public SettingSlider()
    {
        InitializeComponent();
        UpdateSlider();
    }

    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0d);
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), 0d);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 100d);
    public static readonly DependencyProperty StepProperty = Register(nameof(Step), 1d);
    public static readonly DependencyProperty IsLogarithmicProperty = Register(nameof(IsLogarithmic), false);
    public static readonly DependencyProperty LabelProperty = Register(nameof(Label), "");
    public static readonly DependencyProperty UnitProperty = Register(nameof(Unit), "");
    public static readonly DependencyProperty HelpTextProperty = Register(nameof(HelpText), "");
    public static readonly DependencyProperty StopsProperty = Register(nameof(Stops), "");
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public bool IsLogarithmic { get => (bool)GetValue(IsLogarithmicProperty); set => SetValue(IsLogarithmicProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string HelpText { get => (string)GetValue(HelpTextProperty); set => SetValue(HelpTextProperty, value); }
    public string Stops { get => (string)GetValue(StopsProperty); set => SetValue(StopsProperty, value); }

    private static DependencyProperty Register<T>(string name, T value)
        => DependencyProperty.Register(name, typeof(T), typeof(SettingSlider), new PropertyMetadata(value,
            (owner, _) => ((SettingSlider)owner).UpdateSlider()));

    private double Lower => Math.Min(Minimum, Maximum);
    private double Upper => Maximum;

    private void UpdateSlider()
    {
        if (ValueSlider == null || !double.IsFinite(Value)) return;
        _updating = true;
        try
        {
            if (_parsedStops != Stops)
            {
                _parsedStops = Stops;
                _stops = SliderValueScale.ParseStops(Stops);
            }
            // Configure the full range before changing the thumb; initialization never writes back to the draft.
            ValueSlider.Minimum = 0;
            ValueSlider.Maximum = _stops.Length > 0 ? _stops.Length - 1 : 1000;
            ValueSlider.StepFrequency = _stops.Length > 0 || IsLogarithmic ? 1 :
                Math.Max(0.000001, Step / Math.Max(0.000001, Upper - Lower) * 1000);
            ValueSlider.TickFrequency = _stops.Length > 0 ? 1 : 0;
            ValueSlider.TickPlacement = _stops.Length > 0 ? TickPlacement.Outside : TickPlacement.None;
            ValueSlider.Value = _stops.Length > 0 ? SliderValueScale.ClosestStop(Value, _stops) :
                SliderValueScale.ToPosition(Value, Lower, Upper, IsLogarithmic);
            ValueText.Text = Value.ToString("0.######", CultureInfo.CurrentCulture) + (Unit.Length > 0 ? " " + Unit : "");
            var rangeMin = _stops.Length > 0 ? _stops[0] : Lower;
            var rangeMax = _stops.Length > 0 ? _stops[^1] : Upper;
            RangeText.Text = $"{rangeMin:0.##}–{rangeMax:0.##} {Unit}" + (_stops.Length > 0 ? $" · {_stops.Length} 档" : $" · 步长 {Step:0.##} {Unit}");
            var legacy = _stops.Length > 0 ? !_stops.Any(stop => Math.Abs(stop - Value) < 0.000001) : Value < Lower || Value > Upper;
            LegacyValueText.Text = "当前为旧配置值；调整滑块后将使用上述范围" + (_stops.Length > 0 ? "内的固定档位。" : "。");
            LegacyValueText.Visibility = legacy ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(ValueSlider, ValueText.Text);
        }
        finally { _updating = false; }
    }

    private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_updating || ValueSlider == null) return;
        Value = _stops.Length > 0 ? SliderValueScale.StopAtPosition(args.NewValue, _stops) :
            SliderValueScale.FromPosition(args.NewValue, Lower, Upper, Step, IsLogarithmic);
    }

    private void Slider_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Home or VirtualKey.End)
        {
            Value = args.Key == VirtualKey.Home ? (_stops.Length > 0 ? _stops[0] : Lower) : (_stops.Length > 0 ? _stops[^1] : Upper);
            args.Handled = true;
            return;
        }
        var direction = args.Key is VirtualKey.Right or VirtualKey.Up or VirtualKey.PageUp ? 1 :
            args.Key is VirtualKey.Left or VirtualKey.Down or VirtualKey.PageDown ? -1 : 0;
        if (direction == 0) return;
        Value = _stops.Length > 0 ? SliderValueScale.StepStop(Value, _stops, direction) :
            Math.Round(Math.Clamp(Value + direction * Step * (args.Key is VirtualKey.PageUp or VirtualKey.PageDown ? 10 : 1), Lower, Upper), 6);
        args.Handled = true;
    }
}
