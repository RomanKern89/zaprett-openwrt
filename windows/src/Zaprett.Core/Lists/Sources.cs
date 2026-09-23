using System.Text;
using System.Text.Json.Nodes;
using Zaprett.Core.Checks;
using Zaprett.Core.Config;
using Zaprett.Core.Jobs;
using Zaprett.Core.Store;
using Zaprett.Core.Util;
using Zaprett.Core.Validation;
using Zaprett.Core.Text;

namespace Zaprett.Core.Lists;

public sealed record NormalizedList(string Text, int Valid, int Invalid, int Bytes);

/// <summary>URL subscriptions (router sources.uc, contract v1.1 §4): a downloaded subscription becomes an ordinary item
/// src-&lt;name&gt; in ProgramData\installed with "source": "url".</summary>
public sealed class Sources
{
    public const long MaxSourceBytes = 16777216;
    public const int Concurrency = 2;
    public const long LowMemMib = 48;
    public const long ReserveBytes = 256 * 1024;
    /// <summary>An interval that ends within this many seconds after the check still counts as elapsed.</summary>
    public const long DueTolerance = 3600;
    public const int MaxLine = 4096;

    /// <summary>Codes with a text (resource key src.err.&lt;code&gt;).</summary>
    public static readonly IReadOnlyList<string> ErrorCodes = ["too_few_entries", "bad_content", "too_large", "no_space", "write_failed", "invalid_source"];

    static string ErrorText(string code) => ErrorCodes.Contains(code) ? T.S("src.err." + code) : code;

    readonly CoreContext c;

    public Sources(CoreContext context)
    {
        c = context;
    }

    public static string ItemId(string name) => "src-" + name;

    public string StatePath => Path.Combine(c.Paths.InstalledDir, "sources-state.json");

    public (string File, string Manifest) ItemPaths(string name, string type) =>
        (Path.Combine(c.Store.InstalledFilesRoot, ItemTypes.DirOf(type), ItemId(name) + ".txt"),
         Path.Combine(c.Store.InstalledManifestsRoot, ItemTypes.DirOf(type), ItemId(name) + ".json"));

    /* ---------- pure ---------- */

    /// <summary>One line of a downloaded list: the normalized entry, "" for a skipped line (empty, comment) or null for an
    /// invalid one. Masks (*.), inner spaces and IDN that is not in punycode are rejected by the domain check.</summary>
    public static string? NormalizeLine(string kind, string l)
    {
        l = l.Trim();
        if (l.Length == 0 || l[0] is '#' or ';' or '/' or '!')
            return "";
        if (l.Length > MaxLine)
            return null;
        if (kind == "hosts")
        {
            var d = l.ToLowerInvariant();
            return Validate.DomainValid(d) ? d : null;
        }
        return Validate.CidrParse(l)?.Net;
    }

    /// <summary>Normalization of a downloaded list: BOM and CR removed, spaces trimmed, comments and empty lines skipped.</summary>
    public static NormalizedList NormalizeText(string kind, string text)
    {
        text = Validate.StripBom(text);
        var sb = new StringBuilder();
        int valid = 0, invalid = 0;
        foreach (var l in text.Split('\n'))
        {
            var e = NormalizeLine(kind, l);
            if (e == null)
                invalid++;
            else if (e.Length > 0)
            {
                sb.Append(e).Append('\n');
                valid++;
            }
        }
        var res = sb.ToString();
        return new NormalizedList(res, valid, invalid, Files.Utf8NoBom.GetByteCount(res));
    }

    /// <summary>null when the content is acceptable, otherwise too_few_entries or bad_content.</summary>
    public static string? CheckContent(SourceConfig src, NormalizedList n)
    {
        if (n.Valid < src.MinEntries || n.Valid == 0)
            return "too_few_entries";
        var ratio = n.Valid * 1.0 / (n.Valid + n.Invalid);
        return ratio < src.MinValidRatio ? "bad_content" : null;
    }

    public static bool IsDue(SourceConfig src, JsonObject? st, long now)
    {
        var last = R.Long(st?["last_update"]);
        return last == null || now - last >= src.IntervalHours * 3600L - DueTolerance;
    }

    /* ---------- state ---------- */

    JsonObject ReadState() => Files.ReadJson(StatePath, 1048576) ?? [];

    public bool ResetState(string name)
    {
        var st = Files.ReadJson(StatePath, 1048576);
        if (st == null || !st.Remove(name))
            return true;
        return Files.WriteJson(StatePath, st);
    }

    /// <summary>Removes the downloaded item of a subscription (every list type, in case the type was changed).</summary>
    public bool RemoveItem(string name)
    {
        var removed = false;
        foreach (var t in ConfigDefaults.SourceTypes)
        {
            var (file, manifest) = ItemPaths(name, t);
            if (File.Exists(file) || File.Exists(manifest))
            {
                Files.TryDelete(file);
                Files.TryDelete(manifest);
                removed = true;
            }
        }
        ResetState(name);
        return removed;
    }

    /* ---------- update ---------- */

    (string? Error, bool Changed, string? Sha) Install(SourceConfig src, NormalizedList n)
    {
        var (file, manifest) = ItemPaths(src.Name, src.Type);
        var data = Files.Utf8NoBom.GetBytes(n.Text);
        var sum = Files.Sha256Hex(data);
        var old = Files.ReadJson(manifest, 65536);
        if (R.Str(old?["sha256"]) == sum && R.Str(old?["url"]) == src.Url && Files.Size(file) == data.Length)
            return (null, false, sum);
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(c.Paths.InstalledDir));
            if (root != null && new DriveInfo(root).AvailableFreeSpace < data.Length + ReserveBytes)
                return ("no_space", false, null);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // the free space is unknown: the write itself reports a full disk
        }
        if (!Files.AtomicWrite(file, data))
            return ("write_failed", false, null);
        var now = c.P.Clock.Now.ToLocalTime();
        var m = new JsonObject
        {
            ["schema"] = 1, ["id"] = ItemId(src.Name), ["type"] = src.Type, ["name"] = src.Title,
            ["version"] = $"{now:yyyy.MM.dd}", ["author"] = "", ["description"] = src.Url, ["dependencies"] = new JsonArray(),
            ["file"] = file, ["source"] = "url", ["sha256"] = sum, ["installed_at"] = c.Now, ["manifest_url"] = null,
            ["url"] = src.Url, ["entries"] = n.Valid,
        };
        return Files.WriteJson(manifest, m) ? (null, true, sum) : ("write_failed", false, null);
    }

    async Task<(string? Error, string? Detail, int? Entries, bool Changed, string? Sha)> ProcessAsync(SourceConfig src, CancellationToken ct)
    {
        byte[] body;
        try
        {
            body = await c.P.Http.DownloadAsync(src.Url, MaxSourceBytes, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var code = ProbeErrors.Classify(e);
            if (code == "too_large")
                return ("too_large", null, null, false, null);
            return ("download_failed", T.S("src.download_failed", ProbeErrors.TextOf(code), e.Message), null, false, null);
        }
        if (body.LongLength > MaxSourceBytes)
            return ("too_large", null, null, false, null);
        var kind = ItemTypes.All[src.Type].Kind;
        var n = NormalizeText(kind, Encoding.UTF8.GetString(body));
        var err = CheckContent(src, n);
        if (err != null)
            return (err, null, n.Valid, false, null);
        var (ierr, changed, sha) = Install(src, n);
        return (ierr, null, n.Valid, changed, sha);
    }

    /// <summary>names null → every enabled subscription (dueOnly: only those whose interval elapsed).</summary>
    public async Task<JsonObject> UpdateAsync(IReadOnlyList<string>? names, JobContext ctx, bool dueOnly)
    {
        var all = c.Config.Load().Sources;
        var byName = all.ToDictionary(s => s.Name);
        var state = ReadState();
        var now = c.Now;
        var targets = new List<SourceConfig>();
        if (names is { Count: > 0 })
        {
            foreach (var n in names)
            {
                if (!byName.TryGetValue(n, out var s))
                    return R.Fail("not_found", T.S("src.not_found", n));
                targets.Add(s);
            }
        }
        else
        {
            targets.AddRange(all.Where(s => s.Enabled && (!dueOnly || IsDue(s, state[s.Name] as JsonObject, now))));
        }
        if (targets.Count == 0)
            return R.Ok(new JsonObject
            {
                ["updated"] = new JsonArray(), ["unchanged"] = new JsonArray(), ["failed"] = new JsonArray(),
                ["message"] = T.S("src.nothing_to_update"),
            });
        ctx.Progress(5, T.S("src.progress_download", targets.Count(t => t.Valid)));
        var conc = c.P.System.MemoryAvailableMiB is { } mem && mem < LowMemMib ? 1 : Concurrency;
        using var sem = new SemaphoreSlim(conc);
        var done = 0;
        var jobs = targets.Select(async src =>
        {
            if (!src.Valid)
                return ((string?)"invalid_source", (string?)null, (int?)null, false, (string?)null);
            await sem.WaitAsync(ctx.Token).ConfigureAwait(false);
            try
            {
                var r = await ProcessAsync(src, ctx.Token).ConfigureAwait(false);
                ctx.Progress(20 + 75 * Interlocked.Increment(ref done) / targets.Count, T.S("src.progress_process", src.Name));
                return r;
            }
            finally
            {
                sem.Release();
            }
        }).ToList();
        var results = await Task.WhenAll(jobs).ConfigureAwait(false);

        var updated = new JsonArray();
        var unchanged = new JsonArray();
        var failed = new JsonArray();
        for (var i = 0; i < targets.Count; i++)
        {
            var src = targets[i];
            var (err, detail, entries, changed, sha) = results[i];
            var st = state[src.Name] as JsonObject ?? [];
            st["last_check"] = now;
            if (err == null)
            {
                st["last_update"] = now;
                st["sha256"] = sha;
                (changed ? updated : unchanged).Add(src.Name);
                c.P.Log.Info(T.S(changed ? "src.log_updated" : "src.log_unchanged", src.Name, entries));
            }
            if (entries != null)
                st["entries"] = entries;
            st["status"] = err != null ? "error" : "ok";
            st["error"] = err;
            st["message"] = err != null ? detail ?? ErrorText(err) : null;
            state[src.Name] = st;
            if (err != null)
            {
                failed.Add(new JsonObject { ["name"] = src.Name, ["error"] = err, ["message"] = st["message"]?.DeepClone() });
                ctx.Log($"{src.Name}: {R.Str(st["message"])}");
            }
        }
        Files.WriteJson(StatePath, state);
        var res = new JsonObject { ["updated"] = updated, ["unchanged"] = unchanged, ["failed"] = failed };
        if (failed.Count > 0)
            return R.Fail("source_update_failed", T.S("src.update_failed", string.Join(", ",
                failed.OfType<JsonObject>().Select(f => T.S("src.failed_item", R.Str(f["name"]), R.Str(f["message"]))))), res);
        return R.Ok(res);
    }

    /// <summary>"sources.list" (router contract §6.2).</summary>
    public JsonObject List()
    {
        var state = ReadState();
        var idx = c.Store.Scan();
        var o = new JsonArray();
        foreach (var s in c.Config.Load().Sources)
        {
            var item = ConfigDefaults.SourceTypes.Contains(s.Type) ? idx.Get(s.Type, ItemId(s.Name)) : null;
            if (item != null && item.Source != "url")
                item = null;
            var st = state[s.Name] as JsonObject;
            o.Add(new JsonObject
            {
                ["name"] = s.Name, ["enabled"] = s.Enabled, ["title"] = s.Title, ["type"] = s.Type, ["url"] = s.Url,
                ["interval_hours"] = s.IntervalHours, ["min_entries"] = s.MinEntries,
                ["last_update"] = R.Long(st?["last_update"]) ?? item?.InstalledAt,
                ["entries"] = item != null ? c.Store.EntriesOf(item) : null,
                ["size"] = item != null ? Files.Size(item.File) : null,
                ["status"] = !s.Valid ? "invalid" : R.Str(st?["status"]) ?? (item != null ? "ok" : "never"),
                ["error"] = R.Str(st?["error"]) ?? (s.Valid ? null : "invalid_source"),
                ["message"] = R.Str(st?["message"]),
                ["ram_mib"] = s.RamMib, ["item_id"] = ItemId(s.Name), ["downloaded"] = item != null,
            });
        }
        return R.Ok(new JsonObject { ["sources"] = o });
    }
}
