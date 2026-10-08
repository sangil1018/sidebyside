using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SideBySide.App.Infrastructure;

/// <summary>항목 전체 교체 시 Reset 알림 한 번만 보내는 컬렉션 (수만 개 항목에서도 빠름).</summary>
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
