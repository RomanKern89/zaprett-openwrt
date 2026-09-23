using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zaprett.Cli;

/// <summary>What to read from stdin for a command and how to put it into the arguments.</summary>
public enum StdinKind
{
    None,
    /// <summary>Plain text into args.text (strategy save, user set).</summary>
    Text,
    /// <summary>A JSON object merged into args (sources save, settings set).</summary>
    JsonObject,
}

/// <summary>A parsed command line: the IPC method, its arguments and output flags.</summary>
public sealed record CliCommand(string Method, JsonObject Args, bool Json, bool Quiet, StdinKind Stdin = StdinKind.None, int StdinLimit = 0);

/// <summary>Error is a text key of CliText, optionally "key:argument"; Lang is the --lang value (canonical) or null.</summary>
public sealed record CliParseResult(CliCommand? Command, string? Error, bool Help, string? Lang = null);

/// <summary>
/// Router CLI syntax (packages/zaprett/files/usr/share/zaprett/cli.uc) mapped to IPC methods of ARCHITECTURE-WIN §6
/// with the argument names of the router rpcd plugin. Router-only commands (fw, offload, cron, gen-args) are refused.
/// </summary>
public static partial class CliParser
{
    public const int MaxStrategyBytes = 65536;
    public const int MaxStdinBytes = 1048576;

    private static readonly Dictionary<string, string> ValueOptions = new(StringComparer.Ordinal)
    {
        ["--type"] = "type", ["--tail"] = "tail", ["--strategies"] = "strategies", ["--services"] = "services", ["--lang"] = "lang",
    };

    private static readonly HashSet<string> BoolFlags = new(StringComparer.Ordinal)
    {
        "--json", "--foreground", "--quiet", "--all", "--quick", "--full", "--apply-if-better", "--brief", "--exclusive",
        "--if-running", "--if-applied",
    };

    private static readonly HashSet<string> Pages = new(StringComparer.Ordinal) { "overview", "lists", "strategies", "diagnostics" };
    private static readonly HashSet<string> RouterOnly = new(StringComparer.Ordinal) { "fw", "offload", "cron", "gen-args" };

    [GeneratedRegex("^[A-Za-z0-9._-]{1,96}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[a-z0-9_]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceNameRegex();

    /// <summary>Router util.is_id: 1..96 of [A-Za-z0-9._-], not starting with a dot.</summary>
    public static bool IsId(string s) => IdRegex().IsMatch(s) && s[0] != '.';

    public static bool IsServiceRef(string s)
    {
        var parts = s.Split(':');
        return parts.Length is 1 or 2 && parts.All(IsId);
    }

    public static CliParseResult Parse(IReadOnlyList<string> argv)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        var pos = new List<string>();
        for (int i = 0; i < argv.Count; i++)
        {
            string a = argv[i];
            if (BoolFlags.Contains(a))
                flags.Add(a);
            else if (ValueOptions.TryGetValue(a, out var key))
            {
                if (i + 1 >= argv.Count)
                    return Fail("missing_value:" + a);
                opts[key] = argv[++i];
            }
            else if (a.StartsWith("--", StringComparison.Ordinal))
                return Fail("unknown_flag:" + a);
            else
                pos.Add(a);
        }
        bool json = flags.Contains("--json");
        bool quiet = flags.Contains("--quiet");
        string? lang = null;
        if (opts.TryGetValue("lang", out var langOpt) && (lang = CliText.Normalize(langOpt)) is null)
            return Fail("bad_lang:" + langOpt);
        var r = ParseCommand(pos, flags, opts, json, quiet);
        return r with { Lang = lang };
    }

    private static CliParseResult ParseCommand(List<string> pos, HashSet<string> flags, Dictionary<string, string> opts, bool json, bool quiet)
    {
        if (pos.Count == 0 || pos[0] == "help")
            return new CliParseResult(null, null, true);

        var args = new JsonObject();
        if (opts.TryGetValue("strategies", out var strategies))
        {
            var ids = strategies.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (ids.Any(x => !IsId(x)))
                return Fail("bad_strategy_id");
            args["strategies"] = ToArray(ids);
        }
        if (opts.TryGetValue("services", out var services))
        {
            var ids = services.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0)
                return Fail("no_services");
            if (ids.Any(x => !IsId(x)))
                return Fail("bad_service_id");
            args["services"] = ToArray(ids);
        }
        if (opts.TryGetValue("tail", out var tail))
        {
            if (!int.TryParse(tail, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) || n < 1)
                return Fail("bad_tail");
            args["tail"] = n;
        }
        if (opts.TryGetValue("type", out var type))
        {
            if (!IsId(type))
                return Fail("bad_type");
            args["type"] = type;
        }

        CliCommand Cmd(string method, StdinKind stdin = StdinKind.None, int limit = 0) => new(method, args, json, quiet, stdin, limit);
        string c = pos[0];
        string? sub = pos.Count > 1 ? pos[1] : null;
        int n0 = pos.Count;

        if (RouterOnly.Contains(c))
            return Fail("router_only:" + c);

        switch (c)
        {
            case "status" or "start" or "stop" or "restart" or "enable" or "disable" or "check" or "version" or "presets"
                or "conflicts" or "ensure" when n0 == 1:
                return Ok(Cmd(c));
            case "diag" when n0 == 1:
                args["full"] = flags.Contains("--full");
                return Ok(Cmd("diag"));
            case "items" when n0 == 1:
                return Ok(Cmd("items"));
            case "list" when (sub is "enable" or "disable") && n0 == 3 && IsId(pos[2]):
                args["id"] = pos[2];
                return Ok(Cmd("list." + sub));
            case "strategy" when (sub is "set" or "show" or "delete") && n0 == 3 && IsId(pos[2]):
                args["id"] = pos[2];
                return Ok(Cmd("strategy." + sub));
            case "strategy" when sub == "save" && n0 == 3 && IsId(pos[2]):
                args["id"] = pos[2];
                return Ok(Cmd("strategy.save", StdinKind.Text, MaxStrategyBytes));
            case "user" when sub == "get" && n0 == 3 && IsId(pos[2]):
                args["id"] = pos[2];
                return Ok(Cmd("user.get"));
            case "user" when sub == "set" && n0 == 3 && IsId(pos[2]):
                args["id"] = pos[2];
                return Ok(Cmd("user.set", StdinKind.Text, MaxStdinBytes));
            case "mode" when n0 == 2:
                args["mode"] = sub;
                return Ok(Cmd("mode"));
            case "engine" when n0 == 2:
                args["engine"] = sub;
                return Ok(Cmd("engine"));
            case "wizard" when sub == "apply" && n0 >= 3 && pos.Skip(2).All(IsServiceRef):
                args["services"] = ToArray(pos.Skip(2));
                return Ok(Cmd("wizard.apply"));
            case "repo" when sub == "fetch" && n0 == 2:
                return Ok(Cmd("repo.fetch"));
            case "repo" when sub == "list" && n0 == 2:
                return Ok(Cmd("repo.list"));
            case "repo" when sub == "install" && n0 >= 3 && pos.Skip(2).All(IsId):
                args["ids"] = ToArray(pos.Skip(2));
                return Ok(Cmd("repo.install"));
            case "repo" when sub == "remove" && n0 == 3 && IsId(pos[2]):
                args["id"] = pos[2];
                return Ok(Cmd("repo.remove"));
            case "repo" when sub == "upgrade" && pos.Skip(2).All(IsId) && flags.Contains("--all") != (n0 > 2):
                args["ids"] = ToArray(pos.Skip(2));
                args["all"] = flags.Contains("--all");
                return Ok(Cmd("repo.upgrade"));
            case "sources" when sub == "list" && n0 == 2:
                return Ok(Cmd("sources.list"));
            case "sources" when sub == "update" && pos.Skip(2).All(x => SourceNameRegex().IsMatch(x)):
                args["names"] = ToArray(pos.Skip(2));
                return Ok(Cmd("sources.update"));
            case "sources" when sub == "save" && n0 == 3 && SourceNameRegex().IsMatch(pos[2]):
                args["name"] = pos[2];
                return Ok(Cmd("sources.save", StdinKind.JsonObject, 65536));
            case "sources" when sub == "delete" && n0 == 3 && SourceNameRegex().IsMatch(pos[2]):
                args["name"] = pos[2];
                return Ok(Cmd("sources.delete"));
            case "sources" when sub == "defaults" && n0 == 2:
                return Ok(Cmd("sources.defaults"));
            case "test" when sub == "start" && n0 == 2:
                args["quick"] = flags.Contains("--quick");
                args["apply_if_better"] = flags.Contains("--apply-if-better");
                args["exclusive"] = flags.Contains("--exclusive");
                return Ok(Cmd("test.start"));
            case "test" when sub == "status" && n0 == 2:
                args["brief"] = flags.Contains("--brief");
                return Ok(Cmd("test.status"));
            case "test" when sub == "stop" && n0 == 2:
                return Ok(Cmd("test.stop"));
            case "test" when sub == "apply" && n0 == 3 && IsId(pos[2]):
                args["id"] = pos[2];
                return Ok(Cmd("test.apply"));
            case "job" when (sub is "status" or "log" or "cancel") && n0 == 2:
                return Ok(Cmd("job." + sub));
            case "probe" when n0 == 1:
                return Ok(Cmd("probe"));
            case "probe" when sub == "status" && n0 == 2:
                return Ok(Cmd("probe.status"));
            case "monitor" when (sub is "run" or "status") && n0 == 2:
                return Ok(Cmd("monitor." + sub));
            case "log" when n0 == 1:
                return Ok(Cmd("log"));
            case "dns" when sub == "status" && n0 == 2:
                return Ok(Cmd("dns.status"));
            case "dns" when (sub is "setup" or "off") && n0 == 2:
                // "off" returns the adapters to the DNS settings saved before "setup" (also used by the uninstaller)
                args["enable"] = sub == "setup";
                return Ok(Cmd("dns.setup"));
            case "diagnose" when n0 == 1:
                return Ok(Cmd("diagnose"));
            case "diagnose" when sub == "status" && n0 == 2:
                return Ok(Cmd("diagnose.status"));
            case "page" when n0 == 2 && Pages.Contains(sub!):
                args["name"] = sub;
                return Ok(Cmd("page"));
            case "settings" when sub == "get" && n0 == 2:
                return Ok(Cmd("settings.get"));
            case "settings" when sub == "set" && n0 == 2:
                return Ok(Cmd("settings.set", StdinKind.JsonObject, MaxStdinBytes));
            case "update" when (sub is "check" or "install") && n0 == 2:
                return Ok(Cmd("update." + sub));
        }
        return Fail(null);
    }

    private static JsonArray ToArray(IEnumerable<string> items) => new(items.Select(x => (JsonNode)x).ToArray());
    private static CliParseResult Ok(CliCommand c) => new(c, null, false);
    private static CliParseResult Fail(string? message) => new(null, message ?? "", false);   // "" = no detail
}
