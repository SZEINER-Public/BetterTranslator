using System.Collections.ObjectModel;
using System.Collections.Specialized;
using BetterTranslator.App.ViewModels;

namespace BetterTranslator.Mac.Adapters;

public sealed class MacInstallSurface
{
    private readonly FirstRunViewModel _model;

    public MacInstallSurface(FirstRunViewModel model)
    {
        _model = model;
        Items = [];

        Rebuild();

        _model.Items.CollectionChanged += OnItemsChanged;
    }

    public FirstRunViewModel Model => _model;

    public ObservableCollection<InstallItemViewModel> Items { get; }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Items.Clear();

        foreach (var item in _model.Items.Where(Runnable))
        {
            Items.Add(item);
        }
    }

    private static bool Runnable(InstallItemViewModel item) =>
        item.Component.IsSupported && item.Component.Companions.Count == 0;
}
