using System.Text.Json.Nodes;
using Zaprett.Core.Config;
using Zaprett.Core.Platform;
using Zaprett.Core.Util;
using Zaprett.Core.Validation;

namespace Zaprett.Core.Store;

/// <summary>One installed item (local manifest, router contract §3).</summary>
public sealed record StoreItem(
    string Id, string Type, string Name, string Version, string Author, string Description, string? NameEn,
    string? DescriptionEn, IReadOnlyList<string> Dependencies, string File, string Source, string? Sha256,
    long? InstalledAt, string? ManifestUrl, string? ManifestPath, int? EntriesHint);

/// <summary>Result of a scan: items by type and id, and manifests that were rejected.</summary>
public sealed class StoreIndex
{
    public Dictionary<string, Dictionary<string, StoreItem>> Items { get; } = ItemTypes.Order.ToDictionary(t => t, _ => new Dictionary<string, StoreItem>());
    public List<(string Path, string Error)> Errors { get; } = [];

    public StoreItem? Get(string type, string? id) =>
        id != null && Items.TryGetValue(type, out var m) && m.TryGetValue(id, out var it) ? it : null;

    public StoreItem? Find(string id, IEnumerable<string>? types = null)
    {
        foreach (var t in types ?? ItemTypes.Order)
            if (Get(t, id) is { } it)
                return it;
        return null;
    }

    public List<StoreItem> FindAll(string id, IEnumerable<string>? types = null) =>
        (types ?? ItemTypes.Order).Select(t => Get(t, id)).OfType<StoreItem>().ToList();

    /// <summary>Strategies (of both engines) that declare the item as a dependency.</summary>
    public List<string> UsedBy(string id) =>
        ItemTypes.StrategyTypes.SelectMany(t => Items[t].Values).Where(s => s.Dependencies.Contains(id))
            .Select(s => s.Id).Distinct().Order(StringComparer.Ordinal).ToList();
}

/// <summary>Local item store (port of store.uc): bundle (read-only, next to the program) &lt; installed
/// (ProgramData\installed, from the repository and subscriptions) + user items (virtual manifests).</summary>
public sealed class ItemStore
{
    public const int MaxManifestBytes = 65536;
    public const int MaxStrategyBytes = 65536;
    public const long MaxCountBytes = 16777216;

    // Router paths a bundle or repository manifest may name: mapped onto the Windows roots.
    const string RouterBundleFiles = "/usr/share/zaprett/bundle/files/";
    const string RouterEtcFiles = "/etc/zaprett/files/";

    readonly IPaths paths;
    readonly Dictionary<string, (long Size, DateTime Mtime, int? Entries)> entriesCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock cacheGate = new();

    public ItemStore(IPaths paths)
    {
        this.paths = paths;
    }

    public IPaths Paths => paths;

    public string UserListPath(string id) =>
        ItemTypes.UserLists.TryGetValue(id, out var u) ? Path.Combine(paths.UserDir, u.File) : "";

    public bool IsUserList(string id) => ItemTypes.UserLists.ContainsKey(id);

    public string UserStrategyDir(string engine) => Path.Combine(paths.UserDir, "strategies", engine);

    public string UserStrategyPath(string engine, string id) => Path.Combine(UserStrategyDir(engine), id + ".txt");

    public string InstalledFilesRoot => Path.Combine(paths.InstalledDir, "files");

    public string InstalledManifestsRoot => Path.Combine(paths.InstalledDir, "manifests");

    public static string? DepToId(string? dep)
    {
        if (dep == null)
            return null;
        var id = dep;
        if (dep.StartsWith("http:", StringComparison.Ordinal) || dep.StartsWith("https:", StringComparison.Ordinal))
        {
            id = dep[(dep.LastIndexOf('/') + 1)..];
            if (id.EndsWith(".json", StringComparison.Ordinal))
                id = id[..^5];
        }
        return Validate.IsId(id) ? id : null;
    }

    /// <summary>Resolves the "file" of a manifest under &lt;root&gt;\files. Relative: &lt;root&gt;\files\&lt;type dir&gt;\file;
    /// a router absolute path of the bundle or /etc/zaprett is mapped onto the root; a Windows absolute path must lie
    /// inside the root. null when outside.</summary>
    public static string? ResolveManifestFile(string file, string type, string root)
    {
        var filesRoot = Path.Combine(root, "files");
        string candidate;
        if (file.StartsWith('/'))
        {
            string? rest = null;
            if (file.StartsWith(RouterBundleFiles, StringComparison.Ordinal))
                rest = file[RouterBundleFiles.Length..];
            else if (file.StartsWith(RouterEtcFiles, StringComparison.Ordinal))
                rest = file[RouterEtcFiles.Length..];
            if (rest == null)
                return null;
            candidate = Path.Combine(filesRoot, rest.Replace('/', Path.DirectorySeparatorChar));
        }
        else if (Path.IsPathFullyQualified(file))
        {
            candidate = file;
        }
        else
        {
            if (file.Contains(':', StringComparison.Ordinal))
                return null;
            candidate = Path.Combine(filesRoot, ItemTypes.DirOf(type), file.Replace('/', Path.DirectorySeparatorChar));
        }
        return Files.IsUnder(candidate, filesRoot) ? candidate : null;
    }

    static string CleanStr(JsonNode? v, int max)
    {
        var s = R.Str(v)?.Trim() ?? "";
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>Pure: validates a manifest read from &lt;root&gt;\manifests\&lt;dir&gt;\&lt;fileId&gt;.json. Returns the item or
    /// an error code (bad_json, bad_schema, bad_id, id_mismatch, type_mismatch, no_file, file_outside_root, no_sha256).</summary>
    public static (StoreItem? Item, string? Error) ParseManifest(JsonObject? obj, string type, string source, string? fileId, string root)
    {
        if (obj == null)
            return (null, "bad_json");
        if (R.Long(obj["schema"]) != 1)
            return (null, "bad_schema");
        var id = R.Str(obj["id"]);
        if (!Validate.IsId(id))
            return (null, "bad_id");
        if (fileId != null && id != fileId)
            return (null, "id_mismatch");
        if (obj["type"] != null && R.Str(obj["type"]) != type)
            return (null, "type_mismatch");
        var file = R.Str(obj["file"]);
        if (string.IsNullOrEmpty(file))
            return (null, "no_file");
        var resolved = ResolveManifestFile(file, type, root);
        if (resolved == null)
            return (null, "file_outside_root");
        var sha = R.Str(obj["sha256"]);
        if (!Validate.Sha256Valid(sha))
            return (null, "no_sha256");
        var deps = new List<string>();
        if (obj["dependencies"] is JsonArray da)
            foreach (var d in da)
                if (DepToId(R.Str(d)) is { } did && !deps.Contains(did))
                    deps.Add(did);
        var version = R.Str(obj["version"]);
        var name = CleanStr(obj["name"], 128);
        var nameEn = CleanStr(obj["name_en"], 128);
        var descEn = CleanStr(obj["description_en"], 1024);
        var murl = R.Str(obj["manifest_url"]);
        var entries = R.Long(obj["entries"]);
        return (new StoreItem(
            id!, type, name.Length > 0 ? name : id!, Validate.VersionValid(version) ? version! : "0", CleanStr(obj["author"], 128),
            CleanStr(obj["description"], 1024), nameEn.Length > 0 ? nameEn : null, descEn.Length > 0 ? descEn : null, deps,
            resolved, source == "repo" && R.Str(obj["source"]) == "url" ? "url" : source, sha, R.Long(obj["installed_at"]),
            Validate.UrlValid(murl) ? murl : null, null, entries is { } e && e is >= 0 and <= int.MaxValue ? (int)e : null), null);
    }

    public StoreIndex Scan()
    {
        var idx = new StoreIndex();
        foreach (var (source, root) in new[] { ("bundle", paths.BundleDir), ("repo", paths.InstalledDir) })
        {
            foreach (var t in ItemTypes.Order)
            {
                var mdir = Path.Combine(root, "manifests", ItemTypes.DirOf(t));
                if (!Directory.Exists(mdir))
                    continue;
                foreach (var p in Directory.GetFiles(mdir, "*.json").Order(StringComparer.Ordinal))
                {
                    var fid = Path.GetFileNameWithoutExtension(p);
                    if (!Validate.IsId(fid))
                    {
                        idx.Errors.Add((p, "bad_id"));
                        continue;
                    }
                    var (it, err) = ParseManifest(Files.ReadJson(p, MaxManifestBytes), t, source, fid, root);
                    if (it == null)
                    {
                        idx.Errors.Add((p, err!));
                        continue;
                    }
                    if (!File.Exists(it.File))
                    {
                        idx.Errors.Add((p, "file_missing"));
                        continue;
                    }
                    idx.Items[t][it.Id] = it with { ManifestPath = p };
                }
            }
        }
        AddUserItems(idx);
        return idx;
    }

    void AddUserItems(StoreIndex idx)
    {
        foreach (var u in ItemTypes.UserLists.Values)
            idx.Items[u.Type][u.Id] = new StoreItem(u.Id, u.Type, u.Name, "", "", "", u.NameEn, null, [],
                Path.Combine(paths.UserDir, u.File), "user", null, null, null, null, null);
        foreach (var engine in new[] { Engines.Winws, Engines.Winws2 })
        {
            var dir = UserStrategyDir(engine);
            if (!Directory.Exists(dir))
                continue;
            var type = Engines.ItemType(engine);
            foreach (var f in Directory.GetFiles(dir, "*.txt").Order(StringComparer.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (!Validate.IsId(id) || !id.StartsWith("user-", StringComparison.Ordinal))
                    continue;
                idx.Items[type][id] = new StoreItem(id, type, id, "", "", "Своя стратегия", null, "Own strategy", [], f, "user",
                    null, null, null, null, null);
            }
        }
    }

    /// <summary>Bundle manifest of one item (fallback after removal of a repository copy), or null.</summary>
    public StoreItem? BundleItem(string type, string id)
    {
        var p = Path.Combine(paths.BundleDir, "manifests", ItemTypes.DirOf(type), id + ".json");
        var (it, _) = ParseManifest(Files.ReadJson(p, MaxManifestBytes), type, "bundle", id, paths.BundleDir);
        return it != null && File.Exists(it.File) ? it with { ManifestPath = p } : null;
    }

    public static string? ReadStrategyText(StoreItem item) => Files.ReadLimited(item.File, MaxStrategyBytes);

    public static bool IsGzip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 0x1f && fs.ReadByte() == 0x8b;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Entries of a list item (cached by path, size and time); 0 for a missing file; null for other kinds or
    /// files too large or compressed to count.</summary>
    public int? EntriesOf(StoreItem item)
    {
        var kind = ItemTypes.All.TryGetValue(item.Type, out var t) ? t.Kind : null;
        if (kind != "hosts" && kind != "ipset")
            return null;
        var fi = new FileInfo(item.File);
        if (!fi.Exists)
            return 0;
        lock (cacheGate)
        {
            if (entriesCache.TryGetValue(item.File, out var c) && c.Size == fi.Length && c.Mtime == fi.LastWriteTimeUtc)
                return c.Entries;
        }
        int? n = null;
        if (fi.Length == 0)
            n = 0;
        else if (fi.Length <= MaxCountBytes && !IsGzip(item.File))
            n = Validate.CountEntries(Files.ReadLimited(item.File, MaxCountBytes));
        lock (cacheGate)
            entriesCache[item.File] = (fi.Length, fi.LastWriteTimeUtc, n);
        return n;
    }

    public static bool IsActive(StoreItem item, ZaprettConfig cfg, StoreIndex idx)
    {
        var opt = ItemTypes.OptionOf(item.Type);
        if (opt != null)
            return cfg.ListOption(opt).Contains(item.Id);
        if (item.Type == "nfqws")
            return cfg.Strategy == item.Id;
        if (item.Type == "nfqws2")
            return cfg.StrategyWinws2 == item.Id;
        if (item.Type is "bin" or "lua_lib")
        {
            var s = idx.Get(Engines.ItemType(cfg.Engine), cfg.CurrentStrategy());
            return s != null && s.Dependencies.Contains(item.Id);
        }
        return false;
    }

    /// <summary>The "items" answer (router contract §6.2).</summary>
    public JsonArray Describe(StoreIndex idx, ZaprettConfig cfg, string? onlyType)
    {
        var o = new JsonArray();
        foreach (var t in ItemTypes.Order)
        {
            if (onlyType != null && onlyType != t)
                continue;
            foreach (var id in idx.Items[t].Keys.Order(StringComparer.Ordinal))
            {
                var it = idx.Items[t][id];
                o.Add(new JsonObject
                {
                    ["id"] = it.Id,
                    ["type"] = it.Type,
                    ["name"] = it.Name,
                    ["version"] = it.Version,
                    ["author"] = it.Author,
                    ["description"] = it.Description,
                    ["name_en"] = it.NameEn,
                    ["description_en"] = it.DescriptionEn,
                    ["source"] = it.Source,
                    ["file"] = it.File,
                    ["entries"] = EntriesOf(it),
                    ["size"] = Files.Size(it.File) ?? 0,
                    ["active"] = IsActive(it, cfg, idx),
                    ["used_by"] = R.Arr(idx.UsedBy(it.Id)),
                    ["dependencies"] = R.Arr(it.Dependencies),
                    ["supported"] = !ItemTypes.All[t].Unsupported,
                });
            }
        }
        return o;
    }
}
