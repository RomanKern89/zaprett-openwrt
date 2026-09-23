namespace Zaprett.Core.Store;

/// <summary>An item type of the repository (router contract §3): directory, kind and the config option holding it.</summary>
public sealed record ItemType(string Name, string Dir, string Kind, string? Option, bool Unsupported = false);

public sealed record UserList(string Id, string Type, string File, string Name, string NameEn);

public static class ItemTypes
{
    public static readonly IReadOnlyDictionary<string, ItemType> All = new Dictionary<string, ItemType>
    {
        ["list"] = new("list", "lists/include", "hosts", "lists"),
        ["list_exclude"] = new("list_exclude", "lists/exclude", "hosts", "exclude_lists"),
        ["ipset"] = new("ipset", "ipset/include", "ipset", "ipsets"),
        ["ipset_exclude"] = new("ipset_exclude", "ipset/exclude", "ipset", "exclude_ipsets"),
        ["nfqws"] = new("nfqws", "strategies/nfqws", "strategy", null),
        ["nfqws2"] = new("nfqws2", "strategies/nfqws2", "strategy", null),
        ["bin"] = new("bin", "bin", "bin", null),
        ["lua_lib"] = new("lua_lib", "lua", "lua", null),
        ["byedpi"] = new("byedpi", "strategies/byedpi", "strategy", null, Unsupported: true),
    };

    public static readonly IReadOnlyList<string> Order =
        ["list", "list_exclude", "ipset", "ipset_exclude", "nfqws", "nfqws2", "bin", "lua_lib", "byedpi"];

    public static readonly IReadOnlyList<string> ListTypes = ["list", "list_exclude", "ipset", "ipset_exclude"];

    public static readonly IReadOnlyList<string> StrategyTypes = ["nfqws", "nfqws2"];

    public static readonly IReadOnlyDictionary<string, UserList> UserLists = new Dictionary<string, UserList>
    {
        ["user-hosts"] = new("user-hosts", "list", "hosts-include.txt", "Мои домены", "My domains"),
        ["user-hosts-exclude"] = new("user-hosts-exclude", "list_exclude", "hosts-exclude.txt", "Мои домены-исключения", "My excluded domains"),
        ["user-ipset"] = new("user-ipset", "ipset", "ipset-include.txt", "Мои IP-сети", "My IP networks"),
        ["user-ipset-exclude"] = new("user-ipset-exclude", "ipset_exclude", "ipset-exclude.txt", "Мои IP-сети-исключения", "My excluded IP networks"),
    };

    /// <summary>Config option of a list type (lists, exclude_lists, ipsets, exclude_ipsets) or null.</summary>
    public static string? OptionOf(string type) => All.TryGetValue(type, out var t) ? t.Option : null;

    /// <summary>Windows form of a type directory ("lists/include" → "lists\include").</summary>
    public static string DirOf(string type) => All[type].Dir.Replace('/', Path.DirectorySeparatorChar);
}
