using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using K = Zaprett.Ui.Core.Text.Kind;

namespace Zaprett.Ui.Core.ViewModels;

/// <summary>A list set of a service: the core one (Id null) or a variant (router contract §16.3–16.4).</summary>
public sealed record VariantOption(string? Id, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>
/// A service card of the wizard and of the "Services" page: honest mark whether zaprett helps (works yes/partial/no),
/// the choice of the list set, and what the choice means (lists, subscriptions, load).
/// </summary>
public sealed partial class ServiceChoice : ObservableObject
{
    private readonly JsonObject _preset;
    private readonly IReadOnlyDictionary<string, JsonObject> _items;

    public ServiceChoice(JsonObject preset, JsonObject? presetsRoot, IReadOnlyDictionary<string, JsonObject>? items = null)
    {
        _preset = preset;
        _items = items ?? new Dictionary<string, JsonObject>();
        Id = preset.Str("id") ?? "";
        Name = L.Pick(preset, "name") ?? Id;
        Description = L.Pick(preset, "description") ?? "";
        Note = L.Pick(preset, "note") ?? "";
        Works = preset.Str("works") ?? "yes";
        IsHelpless = Works == "no";
        var options = new List<VariantOption> { new(null, L.T("Service.Variant.Core"), L.T("Service.Variant.CoreText")) };
        options.AddRange(preset.Objs("variants").Select(v =>
            new VariantOption(v.Str("id"), L.Pick(v, "name") ?? v.Str("id") ?? "", L.Pick(v, "description") ?? "")));
        Variants = new ObservableCollection<VariantOption>(options);
        var enabledVariant = preset.Str("enabled_variant");
        SelectedVariant = options.FirstOrDefault(o => o.Id == enabledVariant) ?? options[0];
        IsEnabledNow = preset.Bool("enabled") || enabledVariant != null;
        IsPartial = preset.Bool("partially_enabled");
        UsesSources = preset.Strings("sources").Count > 0;
        IsHeavy = preset.Str("tier") == "full";
        var minRam = presetsRoot.Obj("tiers").Obj("full").Int("min_ram_mib", 200);
        HeavyText = IsHeavy ? L.F("Service.Heavy", minRam) : L.T("Service.Light");
        IsSelected = IsEnabledNow;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Note { get; }
    public string Works { get; }
    public bool IsHelpless { get; }
    public bool IsSelectable => !IsHelpless;
    public bool IsEnabledNow { get; }
    public bool IsPartial { get; }
    public bool UsesSources { get; }
    public bool IsHeavy { get; }
    public string HeavyText { get; }
    public bool HasNote => Note.Length > 0;
    public bool HasVariants => Variants.Count > 1;
    public ObservableCollection<VariantOption> Variants { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(StateKind), nameof(HasStateLabel))]
    public partial bool IsSelected { get; set; }

    private VariantOption? _selectedVariant;

    /// <summary>Chosen list set. A ComboBox that is reused for another card writes null while it swaps its items;
    /// null is ignored, so the choice of the person is never lost that way.</summary>
    public VariantOption? SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (value == null && Variants.Count > 0)
                return;
            if (SetProperty(ref _selectedVariant, value))
            {
                OnPropertyChanged(nameof(Contents));
                OnPropertyChanged(nameof(ContentsIds));
            }
        }
    }

    public string WorksKind => Works switch
    {
        "yes" => K.Ok,
        "partial" => K.Warn,
        _ => K.Fail,
    };

    public string WorksLabel => Works switch
    {
        "yes" => L.T("Service.Works.Yes"),
        "partial" => L.T("Service.Works.Partial"),
        _ => L.T("Service.Works.No"),
    };

    /// <summary>
    /// The current state next to the check box only when it differs from the choice ("now off" by a ticked box,
    /// "now on" by a cleared one): a matching state repeated beside the tick reads as a contradiction.
    /// </summary>
    public string StateLabel => IsHelpless ? L.T("Service.State.Helpless")
        : IsPartial ? L.T("Service.State.Partial")
        : IsSelected && !IsEnabledNow ? L.T("Service.State.NowOff")
        : !IsSelected && IsEnabledNow ? L.T("Service.State.NowOn")
        : "";

    public bool HasStateLabel => StateLabel.Length > 0;

    public string StateKind => IsPartial ? K.Warn : K.None;

    public string SourcesText => UsesSources ? L.T("Service.UsesSources") : "";

    private JsonObject ChosenSet => SelectedVariant?.Id == null ? _preset
        : _preset.Objs("variants").FirstOrDefault(v => v.Str("id") == SelectedVariant.Id) ?? _preset;

    /// <summary>What the chosen set turns on: names of the lists and IP networks (id when the item is not installed)
    /// and subscriptions.</summary>
    public string Contents
    {
        get
        {
            var set = ChosenSet;
            var parts = set.Strings("lists").Concat(set.Strings("ipsets")).Select(ItemName)
                .Concat(set.Strings("sources").Select(s => L.F("Service.Source", s))).ToList();
            return parts.Count == 0 ? L.T("Service.NoContents") : L.F("Service.Contents", L.List(parts));
        }
    }

    /// <summary>The ids behind <see cref="Contents"/>, for the tooltip.</summary>
    public string ContentsIds => L.List(ChosenSet.Strings("lists").Concat(ChosenSet.Strings("ipsets")).Concat(ChosenSet.Strings("sources")));

    private string ItemName(string id) => _items.TryGetValue(id, out var item) && L.Pick(item, "name") is { Length: > 0 } name ? name : id;

    /// <summary>Argument of wizard.apply: "id" or "id:variant".</summary>
    public string Reference => SelectedVariant?.Id is { } v ? $"{Id}:{v}" : Id;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && IsHelpless)
            IsSelected = false;
    }

    /// <summary>Cards for all services of the presets answer; helpless ones last.</summary>
    public static List<ServiceChoice> FromPresets(JsonObject? presets, IReadOnlyDictionary<string, JsonObject>? items = null) =>
        presets.Objs("services").Select(s => new ServiceChoice(s, presets, items)).OrderBy(c => c.IsHelpless).ToList();

    /// <summary>First run: nothing enabled yet → defaults.services are pre-selected.</summary>
    public static void Preselect(IReadOnlyList<ServiceChoice> choices, JsonObject? presets)
    {
        if (choices.Any(c => c.IsSelected))
            return;
        var defaults = presets.Obj("defaults").Strings("services");
        foreach (var c in choices)
            c.IsSelected = defaults.Contains(c.Id) && c.IsSelectable;
    }
}
