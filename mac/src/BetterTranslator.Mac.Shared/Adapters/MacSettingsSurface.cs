using System.Collections.ObjectModel;
using System.Collections.Specialized;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Adapters;

public sealed class MacSettingsSurface
{
    private readonly IInferenceBackendSeam _backends;
    private readonly IUpdateTriggerSeam _updates;

    public MacSettingsSurface(SettingsViewModel settings, IInferenceBackendSeam backends, IUpdateTriggerSeam updates)
    {
        Settings = settings;
        _backends = backends;
        _updates = updates;
        Catalogue = [];

        Rebuild();
        settings.Catalogue.CollectionChanged += OnCatalogueChanged;
    }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<CatalogueRow> Catalogue { get; }

    public string RuntimeTitle => _backends.SelectedTitle;

    public string RuntimeSummary => _backends.SelectedSummary;

    public bool HasRuntimeChoice => _backends.Available.Count > 1;

    public bool UpdatesSupported => _updates.IsSupported;

    public string UpdatesUnsupportedReason => _updates.UnsupportedReason;

    private void OnCatalogueChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Catalogue.Clear();

        foreach (var row in Settings.Catalogue.Where(r => r.IsSupported))
        {
            Catalogue.Add(row);
        }
    }
}
