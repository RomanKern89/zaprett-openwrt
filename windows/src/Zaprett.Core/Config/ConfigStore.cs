using System.Text.Json.Nodes;
using Zaprett.Core.Util;

namespace Zaprett.Core.Config;

/// <summary>config.json on disk (ARCHITECTURE-WIN §5): the raw document is the source of truth (like UCI on the
/// router), <see cref="Load"/> gives its normalized form. Writes are atomic (tmp + File.Replace) and keep the previous
/// version in config.json.bak. A damaged file is read from the .bak copy or, failing that, from the defaults (the
/// option "config.json" is then reported in bad_options).</summary>
public sealed class ConfigStore
{
    readonly string path;
    readonly Lock gate = new();

    public ConfigStore(string configFile)
    {
        path = configFile;
    }

    public string FilePath => path;

    /// <summary>The raw document (a copy) and whether it was damaged.</summary>
    public (JsonObject Doc, bool Damaged) ReadRaw()
    {
        lock (gate)
            return ReadRawUnlocked();
    }

    (JsonObject Doc, bool Damaged) ReadRawUnlocked()
    {
        if (!File.Exists(path))
        {
            // a replace that failed half way leaves only the backup
            var onlyBak = File.Exists(path + ".bak") ? Files.ReadJson(path + ".bak", 4 * 1048576) : null;
            return onlyBak != null ? (onlyBak, true) : (ConfigDefaults.Document(), false);
        }
        var doc = Files.ReadJson(path, 4 * 1048576);
        if (doc != null)
            return (doc, false);
        var bak = Files.ReadJson(path + ".bak", 4 * 1048576);
        return (bak ?? ConfigDefaults.Document(), true);
    }

    public ZaprettConfig Load()
    {
        var (doc, damaged) = ReadRaw();
        var cfg = ConfigLoader.Normalize(doc);
        return damaged ? cfg with { BadOptions = [.. cfg.BadOptions, "config.json"] } : cfg;
    }

    /// <summary>Normalized configuration the document would have after the changes (nothing is written).</summary>
    public ZaprettConfig Preview(string section, JsonObject changes)
    {
        var (doc, _) = ReadRaw();
        Apply(doc, section, changes);
        return ConfigLoader.Normalize(doc);
    }

    /// <summary>Applies changes to one section (key → value; null removes the key, so its default applies) and saves.</summary>
    public bool Set(string section, JsonObject changes)
    {
        lock (gate)
        {
            var (doc, _) = ReadRawUnlocked();
            Apply(doc, section, changes);
            return Save(doc);
        }
    }

    /// <summary>Deep-merges a partial document (settings.set): objects are merged, other values replace, null removes.</summary>
    public static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (k, v) in patch)
        {
            if (v == null)
                target.Remove(k);
            else if (v is JsonObject po && target[k] is JsonObject to)
                Merge(to, po);
            else
                target[k] = v.DeepClone();
        }
    }

    public bool SetSource(string name, JsonObject values)
    {
        lock (gate)
        {
            var (doc, _) = ReadRawUnlocked();
            if (doc["sources"] is not JsonObject sources)
            {
                sources = doc.ContainsKey("sources") ? [] : ConfigDefaults.Sources();
                doc["sources"] = sources;
            }
            if (sources[name] is not JsonObject sec)
            {
                sec = [];
                sources[name] = sec;
            }
            foreach (var (k, v) in values)
            {
                if (v == null)
                    sec.Remove(k);
                else
                    sec[k] = v.DeepClone();
            }
            return Save(doc);
        }
    }

    public bool DeleteSource(string name)
    {
        lock (gate)
        {
            var (doc, _) = ReadRawUnlocked();
            if (doc["sources"] is not JsonObject sources)
            {
                if (doc.ContainsKey("sources"))
                    return false;
                sources = ConfigDefaults.Sources();
                doc["sources"] = sources;
            }
            if (!sources.Remove(name))
                return false;
            return Save(doc);
        }
    }

    /// <summary>Deep-merges a partial document into the current one and saves, under the store lock (settings.set):
    /// changes made by others since the caller read the document are kept.</summary>
    public bool MergePatch(JsonObject patch)
    {
        lock (gate)
        {
            var (doc, _) = ReadRawUnlocked();
            Merge(doc, patch);
            return Save(doc);
        }
    }

    public bool Replace(JsonObject doc)
    {
        lock (gate)
            return Save(doc.DeepClone().AsObject());
    }

    static void Apply(JsonObject doc, string section, JsonObject changes)
    {
        if (doc[section] is not JsonObject sec)
        {
            sec = [];
            doc[section] = sec;
        }
        foreach (var (k, v) in changes)
        {
            if (v == null)
                sec.Remove(k);
            else
                sec[k] = v.DeepClone();
        }
    }

    bool Save(JsonObject doc)
    {
        doc["schema"] = 1;
        return Files.WriteJson(path, doc, backup: true);
    }
}
