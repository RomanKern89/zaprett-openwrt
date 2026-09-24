using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.Text;
using K = Zaprett.Ui.Core.Text.Kind;

namespace Zaprett.Ui.Core.ViewModels;

/// <summary>A domain list, IP network list or exclusion list with its switch.</summary>
public sealed partial class ListItem : ObservableObject
{
    private readonly Func<ListItem, bool, Task> _toggle;
    private bool _silent;

    public ListItem(JsonObject item, Func<ListItem, bool, Task> toggle, bool canModify = true)
    {
        _toggle = toggle;
        CanModify = canModify;
        Id = item.Str("id") ?? "";
        Name = L.Pick(item, "name") ?? Id;
        Type = item.Str("type") ?? "";
        Source = item.Str("source") ?? "";
        SourceLabel = UiText.SourceLabel(Source);
        var entries = item.Get("entries") == null ? -1 : item.Long("entries");
        Details = entries >= 0 ? L.F("Lists.Entries", UiText.Number(entries), SourceLabel) : SourceLabel;
        _silent = true;
        IsActive = item.Bool("active");
        _silent = false;
    }

    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public string Source { get; }
    public string SourceLabel { get; }
    public string Details { get; }
    public bool IsUser => Source == "user";
    public bool IsSubscription => Source == "url";
    /// <summary>Follows status.can_modify: the list may be built before the first status arrives.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool CanModify { get; set; }

    public bool CanToggle => !IsSubscription && CanModify;

    [ObservableProperty] public partial bool IsActive { get; set; }

    partial void OnIsActiveChanged(bool value)
    {
        if (!_silent)
            _ = _toggle(this, value);
    }

    /// <summary>Sets the switch back without calling the service (after a failed toggle).</summary>
    public void Revert(bool value)
    {
        _silent = true;
        IsActive = value;
        _silent = false;
    }
}

/// <summary>A subscription to an external list (router contract §4, §6.2 "sources list") with its switch.</summary>
public sealed partial class SourceItem : ObservableObject
{
    private readonly Func<SourceItem, bool, Task>? _toggle;
    private bool _silent;

    public SourceItem(JsonObject s, Func<SourceItem, bool, Task>? toggle = null, bool canModify = true)
    {
        _toggle = toggle;
        CanModify = canModify;
        Raw = s;
        var status = s.Str("status") ?? "never";
        (StatusLabel, Kind) = status switch
        {
            "ok" => (L.T("Source.Status.Ok"), K.Ok),
            "failed" => (L.F("Source.Status.Failed", UiText.Error(s.Str("error"), null)), K.Fail),
            "never" => (L.T("Source.Status.Never"), K.None),
            _ => (status, K.Info),
        };
        Name = s.Str("name") ?? "";
        Title = s.Str("title") ?? Name;
        Url = Validation.MaskUrls(s.Str("url") ?? "");
        TypeLabel = UiText.TypeLabel(s.Str("type"));
        Updated = UiText.Time(s.Long("last_update"));
        Entries = UiText.Number(s.Long("entries"));
        _silent = true;
        IsEnabled = s.Bool("enabled");
        _silent = false;
    }

    /// <summary>The switch and the delete button: false for a user who may only read (follows status.can_modify).</summary>
    [ObservableProperty] public partial bool CanModify { get; set; }

    /// <summary>The answer object of the subscription (to save it back with another "enabled").</summary>
    public JsonObject Raw { get; }
    public string Name { get; }
    public string Title { get; }
    public string Url { get; }
    public string TypeLabel { get; }
    public string Updated { get; }
    public string Entries { get; }
    public string UpdatedText => L.F("Lists.Sources.UpdatedFmt", Updated);
    public string EntriesText => L.F("Lists.Sources.EntriesFmt", Entries);
    public string StatusLabel { get; }
    public string Kind { get; }

    /// <summary>Two-way bound to the switch of this very item, so a reused list element never sends the state of
    /// another subscription.</summary>
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_silent && _toggle != null)
            _ = _toggle(this, value);
    }

    public void Revert(bool value)
    {
        _silent = true;
        IsEnabled = value;
        _silent = false;
    }
}

/// <summary>
/// "Lists": domain lists, IP networks, exclusions and subscriptions; editing of own lists with the line check of
/// the service; import from and export to text files; whitelist/blacklist mode.
/// </summary>
public sealed partial class ListsViewModel(AppState state, INavigator? nav) : PageViewModel(state, nav)
{
    public static readonly string[] UserLists = ["user-hosts", "user-hosts-exclude", "user-ipset", "user-ipset-exclude"];

    /// <summary>Own lists that can be edited, with their labels.</summary>
    public IReadOnlyList<Choice> UserListChoices { get; } =
        UserLists.Select(id => new Choice(id, L.T("Lists.User." + id))).ToList();

    public ObservableCollection<ListItem> Domains { get; } = [];
    public ObservableCollection<ListItem> Networks { get; } = [];
    public ObservableCollection<ListItem> Exclusions { get; } = [];
    public ObservableCollection<SourceItem> Sources { get; } = [];
    public JobProgress Job { get; } = new();

    [ObservableProperty] public partial bool IsBlacklist { get; set; }
    [ObservableProperty] public partial string UserListId { get; set; } = "user-hosts";
    [ObservableProperty] public partial string UserText { get; set; } = "";
    [ObservableProperty] public partial string UserHint { get; set; } = "";
    [ObservableProperty] public partial bool UserDirty { get; set; }
    [ObservableProperty] public partial string NewSourceTitle { get; set; } = "";
    [ObservableProperty] public partial string NewSourceUrl { get; set; } = "";
    [ObservableProperty] public partial bool NewSourceIsIpset { get; set; }
    [ObservableProperty] public partial bool HasSources { get; set; }

    private bool _loadingText;

    /// <summary>The rights may arrive (or change) after the lists were built.</summary>
    protected override void OnStateChanged()
    {
        foreach (var item in Domains.Concat(Networks).Concat(Exclusions))
            item.CanModify = CanModify;
        foreach (var source in Sources)
            source.CanModify = CanModify;
    }

    public override async Task LoadAsync()
    {
        await Try(async () =>
        {
            var page = await State.CallAsync("page", new JsonObject { ["name"] = "lists" });
            Fill(page.OkPart("items") ?? await State.CallAsync("items"), page.OkPart("sources") ?? await State.CallAsync("sources.list"));
            _loadingMode = true;
            IsBlacklist = (page.OkPart("status") ?? State.Status).Str("list_mode") == "blacklist";
            _loadingMode = false;
        }, "Lists.Err.Load");
        await LoadUserTextAsync();
    }

    public void Fill(JsonObject items, JsonObject? sources)
    {
        var all = items.Objs("items").Where(i => i.Str("source") != "url").Select(i => new ListItem(i, ToggleAsync, CanModify)).ToList();
        Replace(Domains, all.Where(i => i.Type == "list").OrderByDescending(i => i.IsActive).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase));
        Replace(Networks, all.Where(i => i.Type == "ipset").OrderByDescending(i => i.IsActive).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase));
        Replace(Exclusions, all.Where(i => i.Type is "list_exclude" or "ipset_exclude").OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase));
        FillSources(sources);
    }

    private void FillSources(JsonObject? sources)
    {
        Replace(Sources, sources.Objs("sources").Select(s => new SourceItem(s, SetSourceEnabledAsync, CanModify)));
        HasSources = Sources.Count > 0;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items)
            target.Add(i);
    }

    private async Task ToggleAsync(ListItem item, bool on)
    {
        var ok = await Try(() => State.CallAsync(on ? "list.enable" : "list.disable", new JsonObject { ["id"] = item.Id }), "Lists.Err.Toggle", busy: false);
        if (!ok)
            item.Revert(!on);
    }

    partial void OnIsBlacklistChanged(bool value)
    {
        if (!_loadingMode)
            _ = SetModeAsync(value);
    }

    private async Task SetModeAsync(bool blacklist)
    {
        if (await Try(() => State.CallAsync("mode", new JsonObject { ["mode"] = blacklist ? "blacklist" : "whitelist" }), "Lists.Err.Mode", busy: false))
            return;
        _loadingMode = true;
        IsBlacklist = !blacklist;
        _loadingMode = false;
    }

    private bool _loadingMode;

    private bool _revertingList;

    partial void OnUserListIdChanged(string? oldValue, string newValue)
    {
        if (_revertingList)
            return;
        _ = SwitchUserListAsync(oldValue);
    }

    /// <summary>Another own list is chosen: unsaved text is not thrown away silently.</summary>
    private async Task SwitchUserListAsync(string? previous)
    {
        if (UserDirty && previous != null &&
            !await State.Platform.ConfirmAsync(L.T("Lists.User.Discard"), L.T("Lists.User.DiscardText"), L.T("Lists.User.DiscardButton")))
        {
            _revertingList = true;
            UserListId = previous;
            _revertingList = false;
            return;
        }
        await LoadUserTextAsync();
    }

    partial void OnUserTextChanged(string value)
    {
        if (!_loadingText)
            UserDirty = true;
    }

    public async Task LoadUserTextAsync()
    {
        UserHint = UserListId.Contains("ipset", StringComparison.Ordinal) ? L.T("Lists.User.HintIp") : L.T("Lists.User.HintDomains");
        await Try(async () =>
        {
            var r = await State.CallAsync("user.get", new JsonObject { ["id"] = UserListId });
            _loadingText = true;
            UserText = r.Str("text") ?? "";
            _loadingText = false;
            UserDirty = false;
        }, "Lists.Err.UserGet", busy: false);
    }

    [RelayCommand]
    private async Task SaveUser()
    {
        var text = UserText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 1024 * 1024)
        {
            ShowMessage(MessageKind.Warning, L.T("Lists.Err.UserSet"), L.T("LineErr.too_large"));
            return;
        }
        var ok = await Try(async () =>
        {
            var r = await State.CallAsync("user.set", new JsonObject { ["id"] = UserListId, ["text"] = text });
            ShowMessage(MessageKind.Success, L.T("Lists.User.Saved"), L.F("Lists.User.SavedText", r.Long("entries")));
        }, "Lists.Err.UserSet");
        if (ok)
            UserDirty = false;
    }

    [RelayCommand]
    private Task Import() => Try(async () =>
    {
        var text = await State.Platform.OpenTextAsync();
        if (text == null)
            return;
        var sep = UserText.Length > 0 && !UserText.EndsWith('\n') ? "\n" : "";
        UserText = UserText + sep + text.Replace("\r\n", "\n", StringComparison.Ordinal);
        ShowMessage(MessageKind.Info, L.T("Lists.User.Imported"), L.T("Lists.User.ImportedText"));
    }, "Lists.User.Import");

    [RelayCommand]
    private Task Export() => Try(async () =>
    {
        var path = await State.Platform.SaveTextAsync(UserListId + ".txt", UserText);
        if (path != null)
            ShowMessage(MessageKind.Success, L.T("Lists.User.Exported"), path);
    }, "Lists.User.Export");

    [RelayCommand]
    private async Task UpdateSources()
    {
        await Try(async () =>
        {
            var (_, job) = await State.RunJobAsync("sources.update", null, Job.Update);
            Job.Update(job);
            FillSources(await State.CallAsync("sources.list"));
        }, "Lists.Err.Sources", busy: false);
    }

    [RelayCommand]
    private async Task AddSource()
    {
        var url = NewSourceUrl.Trim();
        if (!Validation.IsHttpsUrl(url))
        {
            ShowMessage(MessageKind.Warning, L.T("Lists.Source.BadUrl"), L.T("Lists.Source.BadUrlText"));
            return;
        }
        var title = NewSourceTitle.Trim().Length > 0 ? NewSourceTitle.Trim() : new Uri(url).Host;
        var name = UniqueSourceName(SourceName(title), Sources.Select(s => s.Name));
        var ok = await Try(() => State.CallAsync("sources.save", new JsonObject
        {
            ["name"] = name, ["title"] = title, ["type"] = NewSourceIsIpset ? "ipset" : "list", ["url"] = url,
            ["interval_hours"] = 72, ["min_entries"] = 1, ["enabled"] = true,
        }), "Lists.Err.SourceSave");
        if (ok)
        {
            NewSourceTitle = "";
            NewSourceUrl = "";
            await LoadAsync();
            ShowMessage(MessageKind.Success, L.T("Lists.Source.Added"), L.T("Lists.Source.AddedText"));
        }
    }

    /// <summary>sources.save with an existing name updates that subscription, so a new one gets a free name:
    /// "source", "source_2", "source_3"… (at most 32 characters).</summary>
    public static string UniqueSourceName(string name, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(name))
            return name;
        for (var i = 2; ; i++)
        {
            var suffix = "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var candidate = (name.Length + suffix.Length > 32 ? name[..(32 - suffix.Length)] : name) + suffix;
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>Section name of a subscription: ^[a-z0-9_]{1,32}$ from its title.</summary>
    public static string SourceName(string title)
    {
        var slug = new string(title.ToLowerInvariant().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '_').ToArray()).Trim('_');
        while (slug.Contains("__", StringComparison.Ordinal))
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        if (slug.Length == 0)
            slug = "source";
        return slug.Length > 32 ? slug[..32] : slug;
    }

    /// <summary>Switches a subscription on or off: sources.save with its own fields (router contract §6.2).</summary>
    public async Task SetSourceEnabledAsync(SourceItem item, bool enabled)
    {
        var raw = item.Raw;
        var ok = await Try(() => State.CallAsync("sources.save", new JsonObject
        {
            ["name"] = item.Name, ["title"] = raw.Str("title") ?? item.Name, ["type"] = raw.Str("type") ?? "list",
            ["url"] = raw.Str("url") ?? "", ["interval_hours"] = raw.Int("interval_hours", 72), ["min_entries"] = raw.Int("min_entries", 1),
            ["enabled"] = enabled,
        }), "Lists.Err.SourceSave", busy: false);
        if (!ok)
        {
            item.Revert(!enabled);
            return;
        }
        if (await TryListSources() is { } s)
            FillSources(s);
    }

    private async Task<JsonObject?> TryListSources()
    {
        try
        {
            return await State.CallAsync("sources.list");
        }
        catch (ZaprettCallException)
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task DeleteSource(SourceItem? item)
    {
        if (item == null)
            return;
        if (!await State.Platform.ConfirmAsync(L.T("Lists.Source.Delete"), L.F("Lists.Source.DeleteText", item.Title), L.T("Common.Delete")))
            return;
        if (await Try(() => State.CallAsync("sources.delete", new JsonObject { ["name"] = item.Name }), "Lists.Err.SourceDelete"))
            await LoadAsync();
    }
}
