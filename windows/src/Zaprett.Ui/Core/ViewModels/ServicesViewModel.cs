using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;

namespace Zaprett.Ui.Core.ViewModels;

/// <summary>
/// "Services": which services are on, with which list set, what each set contains and how heavy it is.
/// Applying uses wizard.apply with the whole selection (the wizard owns the lists of preset services).
/// </summary>
public sealed partial class ServicesViewModel(AppState state, INavigator? nav) : PageViewModel(state, nav)
{
    public ObservableCollection<ServiceChoice> Services { get; } = [];

    [ObservableProperty] public partial bool IsDirty { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    public override async Task LoadAsync()
    {
        await Try(() => State.RefreshPresetsAsync(), "Services.Err.Load");
        Fill();
    }

    private void Fill()
    {
        foreach (var s in Services)
            s.PropertyChanged -= OnChoiceChanged;
        Services.Clear();
        foreach (var c in ServiceChoice.FromPresets(State.Presets, State.Items))
        {
            c.PropertyChanged += OnChoiceChanged;
            Services.Add(c);
        }
        IsDirty = false;
        UpdateSummary();
    }

    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServiceChoice.IsSelected) or nameof(ServiceChoice.SelectedVariant))
        {
            IsDirty = true;
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        var on = Services.Count(s => s.IsSelected);
        Summary = L.F("Services.Summary", on, Services.Count(s => s.IsSelectable));
    }

    [RelayCommand]
    private async Task Apply()
    {
        var refs = Services.Where(s => s.IsSelected && s.IsSelectable).Select(s => s.Reference).ToList();
        if (refs.Count == 0)
        {
            ShowMessage(MessageKind.Warning, L.T("Wizard.NothingSelected"), L.T("Services.NothingSelectedText"));
            return;
        }
        var ok = await Try(async () =>
        {
            var reply = await State.CallAsync("wizard.apply", new JsonObject { ["services"] = refs.ToJsonArray() });
            var notes = WizardViewModel.Notes(reply, State.Presets);
            ShowMessage(MessageKind.Success, L.T("Services.Applied"), notes.Length > 0 ? notes : L.T("Services.AppliedText"));
        }, "Services.Err.Apply");
        if (ok)
        {
            await Try(() => State.RefreshPresetsAsync(), "Services.Err.Load", busy: false);
            Fill();
        }
    }

    [RelayCommand]
    private void Reset() => Fill();
}
