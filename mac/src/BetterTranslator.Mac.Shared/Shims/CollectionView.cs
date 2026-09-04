using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace System.ComponentModel;

public interface ICollectionView : IEnumerable, INotifyCollectionChanged
{
    Predicate<object>? Filter { get; set; }

    IEnumerable? SourceCollection { get; }

    object? CurrentItem { get; }

    bool IsEmpty { get; }

    void Refresh();

    bool MoveCurrentTo(object item);

    bool MoveCurrentToFirst();
}

public sealed class ListCollectionView : ICollectionView
{
    private readonly IEnumerable _source;
    private List<object> _view = [];

    public ListCollectionView(IEnumerable source)
    {
        _source = source;

        if (source is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += (_, _) => Refresh();
        }

        Rebuild();
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public Predicate<object>? Filter
    {
        get;
        set
        {
            field = value;
            Refresh();
        }
    }

    public IEnumerable? SourceCollection => _source;

    public object? CurrentItem { get; private set; }

    public bool IsEmpty => _view.Count == 0;

    public void Refresh()
    {
        Rebuild();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public bool MoveCurrentTo(object item)
    {
        if (!_view.Contains(item))
        {
            return false;
        }

        CurrentItem = item;
        return true;
    }

    public bool MoveCurrentToFirst()
    {
        CurrentItem = _view.FirstOrDefault();
        return CurrentItem is not null;
    }

    public IEnumerator GetEnumerator() => _view.GetEnumerator();

    private void Rebuild()
    {
        var filter = Filter;

        _view = filter is null
            ? _source.Cast<object>().ToList()
            : _source.Cast<object>().Where(item => filter(item)).ToList();

        if (CurrentItem is not null && !_view.Contains(CurrentItem))
        {
            CurrentItem = null;
        }
    }
}
