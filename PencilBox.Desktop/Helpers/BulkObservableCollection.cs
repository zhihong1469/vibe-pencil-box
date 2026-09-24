using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PencilBox.Desktop.Helpers;

/// <summary>
/// 支持**批量增删只发一次通知**的集合（打印区专用）。
///
/// 为什么需要：日志行会持续涌入（升级时几十行/秒），若每行/每条裁剪都发一次
/// <see cref="INotifyCollectionChanged"/>，WPF 会为每次通知跑一遍布局 —— 明显卡顿。
/// 成批操作只发一次 <see cref="NotifyCollectionChangedAction.Reset"/> 就干净了。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>清空并整体替换（一次 Reset 通知）</summary>
    public void Reset(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        NotifyReset();
    }

    /// <summary>从**头部**裁掉 <paramref name="count"/> 条（一次 Reset 通知）</summary>
    public void TrimHead(int count)
    {
        if (count <= 0)
            return;

        if (count > Items.Count)
            count = Items.Count;

        for (int i = 0; i < count; i++)
            Items.RemoveAt(0);

        NotifyReset();
    }

    private void NotifyReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
