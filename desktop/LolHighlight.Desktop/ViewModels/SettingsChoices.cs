namespace LolHighlight.Desktop.ViewModels;

public sealed record SettingsChoice<T>(T Value, string Label);

public sealed class RecordingEventChoice(string id, string label) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _selected;
    public string Id { get; } = id;
    public string Label { get; } = label;
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
