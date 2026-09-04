using System.Collections;
using System.ComponentModel;

namespace System.Windows.Data;

public sealed class CollectionViewSource
{
    private IEnumerable? _source;
    private ICollectionView? _view;

    public IEnumerable? Source
    {
        get => _source;
        set
        {
            _source = value;
            _view = null;
        }
    }

    public ICollectionView View =>
        _view ??= new ListCollectionView(_source ?? throw new InvalidOperationException("CollectionViewSource.Source was not set."));
}
