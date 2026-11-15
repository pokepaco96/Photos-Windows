using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PhotosWindows.App.ViewModels;

/// <summary>
/// ObservableCollection that can replace all its items with a single Reset notification. Adding the library's ~900
/// day groups one by one raised one change per group and the grid updated after each: ~0.7 s on the UI thread for
/// 8,685 items on Windows 11, on every visit to the library.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
