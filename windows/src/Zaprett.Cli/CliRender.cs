using System.Text;
using System.Text.Json.Nodes;

namespace Zaprett.Cli;

/// <summary>Plain-text output for people. Everything from the service is printed as text, never interpreted.</summary>
public static class CliRender
{
    public static string Render(string? method, JsonObject res, CliText t)
    {
        var sb = new StringBuilder();
        bool ok = res["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        if (!ok)
        {
            string msg = Str(res["message"]) ?? Str(res["error"]) ?? "?";
            sb.Append(t.Error).Append(": ").Append(msg);
            if (Str(res["error"]) is { } code && code != msg)
                sb.Append(" [").Append(code).Append(']');
            return sb.Append('\n').ToString();
        }
        switch (method)
        {
            case "status":
                RenderStatus(sb, res, t);
                return sb.ToString();
            case "log" or "job.log":
                foreach (var l in res["lines"] as JsonArray ?? [])
                    sb.Append(Str(l)).Append('\n');
                return sb.ToString();
            case "strategy.show" or "user.get" when Str(res["text"]) is { } text:
                sb.Append(text);
                if (!text.EndsWith('\n'))
                    sb.Append('\n');
                return sb.ToString();
            case "conflicts":
                var items = res["items"] as JsonArray ?? [];
                if (items.Count == 0)
                    sb.Append(t.NoConflicts).Append('\n');
                foreach (var i in items)
                    sb.Append('[').Append(Str(i?["severity"])).Append("] ").Append(Str(i?["name"])).Append(": ")
                      .Append(Str(i?["detail"])).Append("\n    ").Append(Str(i?["fix"])).Append('\n');
                return sb.ToString();
        }
        if (Str(res["message"]) is { } message)
            return sb.Append(message).Append('\n').ToString();
        bool any = false;
        foreach (var (k, val) in res)
        {
            if (k == "ok" || val is JsonObject or JsonArray)
                continue;
            sb.Append(k).Append(": ").Append(val is null ? "-" : Str(val) ?? val.ToJsonString()).Append('\n');
            any = true;
        }
        if (!any)
            sb.Append(t.Done).Append('\n');
        return sb.ToString();
    }

    private static void RenderStatus(StringBuilder sb, JsonObject res, CliText t)
    {
        bool running = res["running"] is JsonValue r && r.TryGetValue<bool>(out var rb) && rb;
        sb.Append(t.Engine).Append(": ").Append(running ? t.Running : t.Stopped);
        if (res["engine_stats"]?["pid"] is JsonValue pid)
            sb.Append(" (pid ").Append(pid.ToJsonString()).Append(')');
        sb.Append('\n');
        foreach (var key in new[] { "enabled", "engine", "strategy", "list_mode" })
            if (res[key] is JsonValue val)
                sb.Append(key).Append(": ").Append(Str(val) ?? val.ToJsonString()).Append('\n');
        if (res["platform"] is JsonObject p)
            sb.Append(t.Platform).Append(": ").Append(Str(p["os"])).Append(' ').Append(Str(p["version"]) ?? "")
              .Append(" (").Append(Str(p["build"])).Append(", ").Append(Str(p["arch"])).Append(")\n");
        if (res["windivert"]?["foreign"] is JsonArray foreign && foreign.Count > 0)
            sb.Append(t.ForeignWinDivert).Append(": ").Append(string.Join(", ", foreign.Select(Str))).Append('\n');
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n is JsonValue v2 ? v2.ToJsonString() : null;
}
