using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Steamy.Models;

/// <summary>Updates a visible page in one notification rather than rebuilding it for every row.</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public bool ReplaceWith(IEnumerable<T> values)
    {
        var next = values.ToArray();
        if (this.SequenceEqual(next)) return false;
        CheckReentrancy();
        Items.Clear();
        foreach (var item in next) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return true;
    }
}
