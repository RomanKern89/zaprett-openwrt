using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Core.Checks;
using Zaprett.Core.Config;
using Zaprett.Core.Jobs;
using Zaprett.Core.Store;
using Zaprett.Core.Util;
using Zaprett.Core.Validation;
using Zaprett.Core.Text;

namespace Zaprett.Core.Repo;

public sealed record IndexItem(string Id, string Type, string ManifestUrl);

public sealed record RepoManifest(
    string Id, string? IdNote, string Type, string Name, string Version, string Author, string Description,
    IReadOnlyList<string> Dependencies, string ArtifactUrl, string ArtifactSha256, string ManifestUrl);

/// <summary>zaprett-repo client (router repo.uc): index → manifests → artifacts with sha256, cache in run\repo, items in
/// ProgramData\installed. Every download has a hard size limit (router contract v1.3 §14.5).</summary>
public sealed class RepoClient
{
    public const long MaxIndexBytes = 2097152;
    public const long MaxManifestBytes = 65536;
    public const int MaxItems = 2000;
    public const long MaxArtifactBytes = 33554432;
    public const long ReserveBytes = 256 * 1024;
    public const int ArtifactConcurrency = 4;
    public const long LowMemMib = 64;
    public const int ManifestConcurrency = 8;

    readonly CoreContext c;

    public RepoClient(CoreContext context)
    {
        c = context;
    }

    public string CachePath => Path.Combine(c.Paths.RunDir, "repo", "index.json");

    /* ---------- pure ---------- */

    public static (List<IndexItem>? Items, int Skipped) ParseIndex(JsonObject? obj)
    {
        if (obj == null || R.Long(obj["schema"]) != 1 || obj["items"] is not JsonArray arr)
            return (null, 0);
        var items = new List<IndexItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var n in arr)
        {
            var it = n as JsonObject;
            var id = R.Str(it?["id"]);
            var type = R.Str(it?["type"]);
            var url = R.Str(it?["manifest"]);
            if (it == null || !Validate.IsId(id) || type == null || !ItemTypes.All.ContainsKey(type) || !Validate.UrlValid(url) ||
                !seen.Add(type + "/" + id))
            {
                skipped++;
                continue;
            }
            items.Add(new IndexItem(id!, type, url!));
            if (items.Count >= MaxItems)
                break;
        }
        return (items, skipped);
    }

    static string Str(JsonNode? n, int max)
    {
        var s = R.Str(n)?.Trim() ?? "";
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>Validates a repository manifest (schema 1). The index id is authoritative; a different id in the manifest
    /// is only noted.</summary>
    public static (RepoManifest? Manifest, string? Error) ParseManifest(JsonObject? obj, IndexItem idx)
    {
        if (obj == null || R.Long(obj["schema"]) != 1)
            return (null, "bad_manifest");
        var id = R.Str(obj["id"]);
        if (id == null)
            return (null, "id_mismatch");
        var note = id != idx.Id ? T.S("repo.id_note", id.Length > 100 ? id[..100] : id) : null;
        var version = R.Str(obj["version"]);
        if (!Validate.VersionValid(version))
            return (null, "bad_version");
        var art = obj["artifact"] as JsonObject;
        var sha = R.Str(art?["sha256"])?.ToLowerInvariant() ?? "";
        var aurl = R.Str(art?["url"]);
        if (art == null || !Validate.UrlValid(aurl) || !Validate.Sha256Valid(sha))
            return (null, "bad_artifact");
        var deps = new List<string>();
        if (obj["dependencies"] != null)
        {
            if (obj["dependencies"] is not JsonArray da)
                return (null, "bad_dependencies");
            foreach (var d in da)
            {
                var u = R.Str(d);
                if (!Validate.UrlValid(u))
                    return (null, "bad_dependencies");
                if (!deps.Contains(u!))
                    deps.Add(u!);
            }
        }
        var name = Str(obj["name"], 128);
        return (new RepoManifest(idx.Id, note, idx.Type, name.Length > 0 ? name : idx.Id, version!, Str(obj["author"], 128),
            Str(obj["description"], 1024), deps, aurl!, sha, idx.ManifestUrl), null);
    }

    /// <summary>Bundle items are replaced only by a strictly newer version; repository items also when the version is
    /// equal but the artifact differs.</summary>
    public static bool IsUpdate(StoreItem? installed, RepoManifest? m)
    {
        if (installed == null || m == null)
            return false;
        var cmp = Validate.VersionCmp(m.Version, installed.Version);
        return cmp > 0 || (cmp == 0 && installed.Source == "repo" && installed.Sha256 != null && installed.Sha256 != m.ArtifactSha256);
    }

    /// <summary>Dependency closure in install order (dependencies first); a cycle's back edge is ignored.</summary>
    public static (List<JsonObject>? Order, string? Item, string? Url) ResolveOrder(IEnumerable<JsonObject> roots,
        IReadOnlyDictionary<string, JsonObject> byUrl)
    {
        var order = new List<JsonObject>();
        var state = new Dictionary<string, string>();
        (string, string)? err = null;
        void Visit(JsonObject item)
        {
            if (err != null)
                return;
            var key = R.Str(item["type"]) + "/" + R.Str(item["id"]);
            if (state.ContainsKey(key))
                return;
            state[key] = "visiting";
            foreach (var u in R.Strings(item["manifest"]?["dependencies"]))
            {
                if (!byUrl.TryGetValue(u, out var dep) || dep["manifest"] == null)
                {
                    err = (R.Str(item["id"])!, u);
                    return;
                }
                Visit(dep);
            }
            state[key] = "done";
            order.Add(item);
        }
        foreach (var r in roots)
            Visit(r);
        return err is { } e ? (null, e.Item1, e.Item2) : (order, null, null);
    }

    static JsonObject ManifestJson(RepoManifest m) => new()
    {
        ["id"] = m.Id, ["id_note"] = m.IdNote, ["type"] = m.Type, ["name"] = m.Name, ["version"] = m.Version, ["author"] = m.Author,
        ["description"] = m.Description, ["dependencies"] = R.Arr(m.Dependencies),
        ["artifact"] = new JsonObject { ["url"] = m.ArtifactUrl, ["sha256"] = m.ArtifactSha256 }, ["manifest_url"] = m.ManifestUrl,
    };

    static RepoManifest? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o)
            return null;
        return new RepoManifest(R.Str(o["id"]) ?? "", R.Str(o["id_note"]), R.Str(o["type"]) ?? "", R.Str(o["name"]) ?? "",
            R.Str(o["version"]) ?? "0", R.Str(o["author"]) ?? "", R.Str(o["description"]) ?? "", R.Strings(o["dependencies"]),
            R.Str(o["artifact"]?["url"]) ?? "", R.Str(o["artifact"]?["sha256"]) ?? "", R.Str(o["manifest_url"]) ?? "");
    }

    /* ---------- network ---------- */

    async Task<(byte[]? Body, string? Code, string? Message)> DownloadAsync(string url, long max, int timeoutSec, CancellationToken ct)
    {
        try
        {
            var b = await c.P.Http.DownloadAsync(url, max, TimeSpan.FromSeconds(timeoutSec), ct).ConfigureAwait(false);
            return b.LongLength > max ? (null, "too_large", null) : (b, null, null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return (null, ProbeErrors.Classify(e), e.Message);
        }
    }

    public JsonObject? ReadCache()
    {
        var cache = Files.ReadJson(CachePath, 16777216);
        return cache?["items"] is JsonArray ? cache : null;
    }

    /// <summary>Downloads index.json and all manifests into the cache.</summary>
    public async Task<JsonObject> FetchAsync(ZaprettConfig cfg, JobContext ctx)
    {
        var url = cfg.Repo.Url;
        ctx.Progress(2, T.S("repo.progress_index", url));
        var (body, code, msg) = await DownloadAsync(url, MaxIndexBytes, 30, ctx.Token).ConfigureAwait(false);
        if (code == "too_large")
            return R.Fail("too_large", T.S("repo.index_too_large"), new JsonObject { ["url"] = url });
        if (body == null || body.Length < 2)
            return R.Fail("download_failed", T.S("repo.index_download_failed", ProbeErrors.TextOf(code ?? "too_small"), msg),
                new JsonObject { ["url"] = url });
        var (items, skipped) = ParseIndex(R.ParseObject(Encoding.UTF8.GetString(body)));
        if (items == null)
            return R.Fail("bad_index", T.S("repo.bad_index"));
        ctx.Token.ThrowIfCancellationRequested();

        ctx.Progress(10, T.S("repo.progress_manifests", items.Count));
        using var sem = new SemaphoreSlim(ManifestConcurrency);
        var entries = await Task.WhenAll(items.Select(async it =>
        {
            await sem.WaitAsync(ctx.Token).ConfigureAwait(false);
            try
            {
                var entry = new JsonObject
                {
                    ["id"] = it.Id, ["type"] = it.Type, ["manifest_url"] = it.ManifestUrl, ["manifest"] = null, ["error"] = null,
                };
                var (mb, mcode, _) = await DownloadAsync(it.ManifestUrl, MaxManifestBytes, 20, ctx.Token).ConfigureAwait(false);
                if (mb == null || mb.Length < 2)
                {
                    entry["error"] = mcode ?? "too_small";
                    return entry;
                }
                var (m, err) = ParseManifest(R.ParseObject(Encoding.UTF8.GetString(mb)), it);
                if (m == null)
                    entry["error"] = err;
                else
                    entry["manifest"] = ManifestJson(m);
                return entry;
            }
            finally
            {
                sem.Release();
            }
        })).ConfigureAwait(false);
        var errors = entries.Count(e => e["error"] != null);
        var cache = new JsonObject
        {
            ["fetched_at"] = c.Now, ["url"] = url, ["items"] = R.Arr(entries.Select(e => (JsonNode?)e)), ["errors"] = errors, ["skipped"] = skipped,
        };
        if (!Files.WriteJson(CachePath, cache))
            return R.Fail("write_failed", T.S("repo.cache_failed"));
        ctx.Progress(100, T.S("repo.progress_index_done", entries.Length, errors));
        return R.Ok(new JsonObject { ["fetched_at"] = cache["fetched_at"]!.DeepClone(), ["total"] = entries.Length, ["errors"] = errors });
    }

    /// <summary>"repo.list" from the cache.</summary>
    public JsonObject List(ZaprettConfig cfg, string? onlyType)
    {
        var cache = ReadCache();
        if (cache == null)
            return R.Ok(new JsonObject { ["fetched_at"] = null, ["url"] = cfg.Repo.Url, ["items"] = new JsonArray() });
        var idx = c.Store.Scan();
        var o = new JsonArray();
        foreach (var e in ((JsonArray)cache["items"]!).OfType<JsonObject>())
        {
            var type = R.Str(e["type"]) ?? "";
            if ((onlyType != null && type != onlyType) || !ItemTypes.All.ContainsKey(type))
                continue;
            var m = FromJson(e["manifest"]);
            var inst = idx.Get(type, R.Str(e["id"]));
            if (inst?.Source == "user")
                inst = null;
            o.Add(new JsonObject
            {
                ["id"] = e["id"]?.DeepClone(), ["type"] = type, ["name"] = m?.Name ?? R.Str(e["id"]), ["version"] = m?.Version,
                ["author"] = m?.Author ?? "", ["description"] = m?.Description ?? "", ["installed"] = inst != null,
                ["installed_version"] = inst?.Version, ["installed_source"] = inst?.Source, ["update_available"] = IsUpdate(inst, m),
                ["supported"] = !ItemTypes.All[type].Unsupported, ["size"] = inst != null ? Files.Size(inst.File) : null,
                ["error"] = e["error"]?.DeepClone(),
            });
        }
        return R.Ok(new JsonObject { ["fetched_at"] = cache["fetched_at"]?.DeepClone(), ["url"] = cache["url"]?.DeepClone(), ["items"] = o });
    }

    static string ExtOf(string url)
    {
        var name = url.Split('?')[0];
        name = name[(name.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        var ext = dot >= 0 ? name[(dot + 1)..] : "";
        return ext.Length is >= 1 and <= 8 && ext.All(char.IsAsciiLetterOrDigit) ? ext.ToLowerInvariant() : "txt";
    }

    JsonObject InstallArtifact(RepoManifest m, byte[] body, List<string> depIds)
    {
        var fdir = Path.Combine(c.Store.InstalledFilesRoot, ItemTypes.DirOf(m.Type));
        var mdir = Path.Combine(c.Store.InstalledManifestsRoot, ItemTypes.DirOf(m.Type));
        var dst = Path.Combine(fdir, m.Id + "." + ExtOf(m.ArtifactUrl));
        var mpath = Path.Combine(mdir, m.Id + ".json");
        var old = Files.ReadJson(mpath, MaxManifestBytes);
        if (!Files.AtomicWrite(dst, body) || Files.Sha256File(dst) != m.ArtifactSha256)
            return R.Fail("write_failed", T.S("repo.write_failed", dst));
        var local = new JsonObject
        {
            ["schema"] = 1, ["id"] = m.Id, ["type"] = m.Type, ["name"] = m.Name, ["version"] = m.Version, ["author"] = m.Author,
            ["description"] = m.Description, ["dependencies"] = R.Arr(depIds), ["file"] = dst, ["source"] = "repo",
            ["sha256"] = m.ArtifactSha256, ["installed_at"] = c.Now, ["manifest_url"] = m.ManifestUrl,
        };
        if (!Files.WriteJson(mpath, local))
            return R.Fail("write_failed", T.S("repo.manifest_write_failed", m.Id));
        var oldFile = R.Str(old?["file"]);
        if (oldFile != null && !string.Equals(oldFile, dst, StringComparison.OrdinalIgnoreCase) && Files.IsUnder(oldFile, c.Store.InstalledFilesRoot))
            Files.TryDelete(oldFile);
        return R.Ok(new JsonObject { ["file"] = dst });
    }

    /// <summary>Installs items with their dependencies. onlyUpdates: roots are installed only when newer; upgradeDeps:
    /// installed dependencies are refreshed too.</summary>
    /// <param name="allowCode">false for a caller that is not an administrator: Lua libraries (code that winws2 runs as
    /// SYSTEM) are refused.</param>
    public async Task<JsonObject> InstallAsync(ZaprettConfig cfg, IReadOnlyList<string> ids, JobContext ctx, bool upgradeDeps = false,
        bool onlyUpdates = false, bool allowCode = true)
    {
        var cache = ReadCache();
        if (cache == null)
        {
            var f = await FetchAsync(cfg, ctx).ConfigureAwait(false);
            if (!R.IsOk(f))
                return f;
            cache = ReadCache()!;
        }
        var all = ((JsonArray)cache["items"]!).OfType<JsonObject>().ToList();
        var byUrl = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var e in all)
            if (R.Str(e["manifest_url"]) is { } u)
                byUrl[u] = e;

        var roots = new List<JsonObject>();
        foreach (var id in ids)
        {
            if (!Validate.IsId(id))
                return R.Fail("bad_id", T.S("items.bad_id_value", id));
            var cands = all.Where(e => R.Str(e["id"]) == id && e["manifest"] != null &&
                ItemTypes.All.TryGetValue(R.Str(e["type"]) ?? "", out var t) && !t.Unsupported).ToList();
            if (cands.Count == 0)
            {
                var any = all.FirstOrDefault(e => R.Str(e["id"]) == id);
                if (any != null && ItemTypes.All.TryGetValue(R.Str(any["type"]) ?? "", out var at) && at.Unsupported)
                    return R.Fail("unsupported_item", T.S("repo.unsupported", id, at.Name));
                return R.Fail("not_in_repo", T.S("repo.not_in_repo", id));
            }
            if (cands.Count > 1)
                return R.Fail("ambiguous_id", T.S("repo.ambiguous", id, string.Join(", ", cands.Select(e => R.Str(e["type"])))));
            roots.Add(cands[0]);
        }
        var (order, depItem, depUrl) = ResolveOrder(roots, byUrl);
        if (order == null)
            return R.Fail("dep_not_found", T.S("repo.dep_not_found", depItem, depUrl));

        var idx = c.Store.Scan();
        var rootKeys = roots.Select(e => R.Str(e["type"]) + "/" + R.Str(e["id"])).ToHashSet();
        var plan = new List<RepoManifest>();
        var skipped = new JsonArray();
        foreach (var e in order)
        {
            var m = FromJson(e["manifest"])!;
            var inst = idx.Get(m.Type, m.Id);
            if (inst?.Source == "user")
                inst = null;
            var isRoot = rootKeys.Contains(m.Type + "/" + m.Id);
            if (isRoot && !onlyUpdates)
                plan.Add(m);
            else if (inst == null || ((isRoot || upgradeDeps) && IsUpdate(inst, m)))
                plan.Add(m);
            else
                skipped.Add(m.Id);
        }
        if (plan.Count == 0)
            return R.Ok(new JsonObject { ["installed"] = new JsonArray(), ["skipped"] = skipped, ["message"] = T.S("repo.all_installed") });
        if (!allowCode && plan.Any(m => m.Type == "lua_lib"))
            return R.Fail("access_denied", T.S("call.admin_only"));

        ctx.Progress(20, T.S("repo.progress_files", plan.Count));
        var conc = c.P.System.MemoryAvailableMiB is { } mem && mem < LowMemMib ? 1 : ArtifactConcurrency;
        using var sem = new SemaphoreSlim(conc);
        var downloads = await Task.WhenAll(plan.Select(async m =>
        {
            await sem.WaitAsync(ctx.Token).ConfigureAwait(false);
            try
            {
                return await DownloadAsync(m.ArtifactUrl, MaxArtifactBytes, 30, ctx.Token).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        })).ConfigureAwait(false);
        ctx.Token.ThrowIfCancellationRequested();

        long total = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            var (body, code, msg) = downloads[i];
            if (code == "too_large")
                return R.Fail("too_large", T.S("repo.file_too_large", plan[i].Id));
            if (body == null)
                return R.Fail("download_failed", T.S("repo.download_failed", plan[i].Id, ProbeErrors.TextOf(code), msg));
            if (Files.Sha256Hex(body) != plan[i].ArtifactSha256)
                return R.Fail("sha256_mismatch", T.S("repo.sha_mismatch", plan[i].Id));
            total += body.LongLength;
        }
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(c.Paths.InstalledDir));
            if (root != null && new DriveInfo(root).AvailableFreeSpace is var free && free < total + ReserveBytes)
                return R.Fail("no_space", T.S("repo.no_space", (total + ReserveBytes) / 1024, free / 1024));
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // unknown free space: the write reports a full disk
        }

        var installed = new JsonArray();
        for (var i = 0; i < plan.Count; i++)
        {
            var m = plan[i];
            ctx.Progress(40 + 55 * i / plan.Count, T.S("repo.progress_install", m.Id));
            var depIds = m.Dependencies.Where(byUrl.ContainsKey).Select(u => R.Str(byUrl[u]["id"])!).ToList();
            var r = InstallArtifact(m, downloads[i].Body!, depIds);
            if (!R.IsOk(r))
            {
                r["installed"] = installed;
                return r;
            }
            installed.Add(m.Id);
            c.P.Log.Info(T.S("repo.log_installed", m.Type, m.Id, m.Version));
        }
        return R.Ok(new JsonObject { ["installed"] = installed, ["skipped"] = skipped, ["types"] = R.Arr(plan.Select(m => m.Type)) });
    }

    public JsonObject Remove(ZaprettConfig cfg, string id)
    {
        if (!Validate.IsId(id))
            return R.Fail("bad_id", T.S("items.bad_id_value", id));
        var idx = c.Store.Scan();
        var all = idx.FindAll(id);
        var mine = all.Where(it => it.Source == "repo").ToList();
        if (mine.Count == 0)
        {
            if (all.Any(it => it.Source == "url"))
                return R.Fail("readonly_item", T.S("repo.readonly_source", id));
            if (all.Count > 0)
                return R.Fail("readonly_item", T.S("repo.readonly_item", id));
            return R.Fail("not_installed", T.S("repo.not_installed", id));
        }
        if (mine.Count > 1)
            return R.Fail("ambiguous_id", T.S("repo.ambiguous_installed", id));
        var it = mine[0];
        var fallback = c.Store.BundleItem(it.Type, id);
        if (fallback == null)
        {
            var users = idx.UsedBy(id).Where(x => x != id).ToList();
            if (users.Count > 0)
                return R.Fail("item_in_use", T.S("repo.in_use", id, string.Join(", ", users)),
                    new JsonObject { ["used_by"] = R.Arr(users) });
            if (ItemStore.IsActive(it, cfg, idx))
                return R.Fail("item_active", T.S("repo.active", id));
        }
        if (Files.IsUnder(it.File, c.Store.InstalledFilesRoot))
            Files.TryDelete(it.File);
        if (it.ManifestPath != null)
            Files.TryDelete(it.ManifestPath);
        c.P.Log.Info(T.S("repo.log_removed", it.Type, id));
        return R.Ok(new JsonObject { ["removed"] = id, ["type"] = it.Type, ["fallback"] = fallback != null ? "bundle" : null });
    }

    /// <summary>Upgrades installed items; ids null → every item installed from the repository.</summary>
    public async Task<JsonObject> UpgradeAsync(ZaprettConfig cfg, IReadOnlyList<string>? ids, JobContext ctx, bool noFetch = false,
        bool allowCode = true)
    {
        if (!noFetch)
        {
            var f = await FetchAsync(cfg, ctx).ConfigureAwait(false);
            if (!R.IsOk(f))
                return f;
        }
        var cache = ReadCache();
        if (cache == null)
            return R.Fail("repo_not_fetched", T.S("repo.not_fetched"));
        var idx = c.Store.Scan();
        var targets = new List<string>();
        var upToDate = new List<string>();
        foreach (var e in ((JsonArray)cache["items"]!).OfType<JsonObject>())
        {
            var m = FromJson(e["manifest"]);
            if (m == null || !ItemTypes.All.TryGetValue(m.Type, out var t) || t.Unsupported)
                continue;
            var inst = idx.Get(m.Type, m.Id);
            if (inst == null || inst.Source == "user" || (ids == null && inst.Source != "repo") || (ids != null && !ids.Contains(m.Id)))
                continue;
            (IsUpdate(inst, m) ? targets : upToDate).Add(m.Id);
        }
        if (ids != null)
            foreach (var id in ids)
                if (!targets.Contains(id) && !upToDate.Contains(id))
                    return R.Fail("not_installed", T.S("repo.not_installed_or_missing", id));
        if (targets.Count == 0)
            return R.Ok(new JsonObject { ["installed"] = new JsonArray(), ["up_to_date"] = R.Arr(upToDate), ["message"] = T.S("repo.no_updates") });
        var r = await InstallAsync(cfg, targets.Distinct().ToList(), ctx, upgradeDeps: true, allowCode: allowCode).ConfigureAwait(false);
        r["up_to_date"] = R.Arr(upToDate);
        return r;
    }
}
