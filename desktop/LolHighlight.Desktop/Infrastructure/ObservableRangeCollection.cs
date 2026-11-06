using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LolHighlight.Desktop.Infrastructure;

public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        var replacement = items.ToList();
        CheckReentrancy();
        Items.Clear();
        foreach (var item in replacement) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
