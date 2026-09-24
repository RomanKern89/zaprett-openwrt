using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zaprett.Ipc;

namespace Zaprett.Cli;

/// <summary>zaprett.exe: parse, call the service over the pipe, print. Exit codes: 0 ok, 1 the method failed,
/// 2 bad arguments, 3 the service is not available.</summary>
public static class CliApp
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitUnavailable = 3;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Output language (ARCHITECTURE-WIN §12.2): --lang, else ui.language from settings.get, else Russian.
    /// The language is also passed to the call as "lang" so that the core's messages match.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> argv, Func<IZaprettClient> connect, Stream stdin,
        TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        await using var client = connect();
        bool jsonMode = argv.Contains("--json");
        var parsed = CliParser.Parse(argv);
        bool serviceDown = false;
        string? lang = parsed.Lang;
        // JSON output of a command needs no texts: the core picks ui.language itself
        if (lang is null && !(jsonMode && parsed.Command is not null))
        {
            try
            {
                lang = await ConfiguredLanguageAsync(client, ct).ConfigureAwait(false);
            }
            catch (ZaprettUnavailableException)
            {
                serviceDown = true;
            }
        }
        var text = CliText.Load(lang);
        if (parsed.Help)
        {
            if (jsonMode)
                await stdout.WriteLineAsync(new JsonObject { ["ok"] = true, ["usage"] = text.Usage }.ToJsonString(Pretty)).ConfigureAwait(false);
            else
                await stdout.WriteAsync(text.Usage).ConfigureAwait(false);
            return CliParser.IsHelpRequest(argv) ? ExitOk : ExitUsage;
        }
        if (parsed.Command is not { } cmd)
        {
            await Print(Fail("usage", text.UsageError(parsed.Error)), jsonMode, false, stdout, stderr, text, null).ConfigureAwait(false);
            return ExitUsage;
        }

        var args = cmd.Args;
        if (lang is not null)
            args["lang"] = text.Language;
        if (cmd.Stdin != StdinKind.None)
        {
            // bytes, not the console encoding: PowerShell 5.1 pipes ASCII, UTF-8 with a BOM or UTF-16 (StdinText)
            string? input = await StdinText.ReadAsync(stdin, cmd.StdinLimit, ct).ConfigureAwait(false);
            if (input is null)
            {
                await Print(Fail("too_large", text.InputTooLarge(cmd.StdinLimit)), cmd.Json, false, stdout, stderr, text, null).ConfigureAwait(false);
                return ExitUsage;
            }
            if (cmd.Stdin == StdinKind.Text)
                args["text"] = input;
            else
            {
                JsonObject? obj;
                string why;
                try
                {
                    obj = input.Length == 0 ? null : JsonNode.Parse(input) as JsonObject;
                    why = input.Length == 0 ? text.EmptyInput : text.NotAnObject;
                }
                catch (JsonException e)
                {
                    obj = null;
                    why = e.Message;
                }
                if (obj is null)
                {
                    await Print(Fail("usage", text.InputNotJsonDetail(why)), cmd.Json, false, stdout, stderr, text, null).ConfigureAwait(false);
                    return ExitUsage;
                }
                foreach (var (k, item) in obj.ToList())
                {
                    obj.Remove(k);
                    // the name from the command line wins over one in the input
                    if (!args.ContainsKey(k))
                        args[k] = item;
                }
            }
        }

        JsonObject res;
        try
        {
            // the language lookup already found the service down: do not wait for the connect timeout twice
            if (serviceDown)
                throw new ZaprettUnavailableException("zaprett service is not available");
            res = await client.CallAsync(cmd.Method, args, ct).ConfigureAwait(false);
        }
        catch (ZaprettUnavailableException e)
        {
            await Print(Fail("service_unavailable", text.Unavailable(e.Message)), cmd.Json, false, stdout, stderr, text, null).ConfigureAwait(false);
            return ExitUnavailable;
        }
        bool ok = res["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        await Print(res, cmd.Json, cmd.Quiet, stdout, stderr, text, cmd.Method).ConfigureAwait(false);
        if (ok)
            return ExitOk;
        return res["error"] is JsonValue ev && ev.TryGetValue<string>(out var error) && error == "usage" ? ExitUsage : ExitFailed;
    }

    /// <summary>ui.language of config.json through settings.get; null when it is not set or unknown.</summary>
    private static async Task<string?> ConfiguredLanguageAsync(IZaprettClient client, CancellationToken ct)
    {
        var r = await client.CallAsync("settings.get", null, ct).ConfigureAwait(false);
        return r["config"]?["ui"]?["language"] is JsonValue v && v.TryGetValue<string>(out var l) ? CliText.Normalize(l) : null;
    }

    private static JsonObject Fail(string error, string message) => new() { ["ok"] = false, ["error"] = error, ["message"] = message };

    private static async Task Print(JsonObject res, bool json, bool quiet, TextWriter stdout, TextWriter stderr, CliText text, string? method)
    {
        bool ok = res["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        if (json)
        {
            await stdout.WriteLineAsync(res.ToJsonString(Pretty)).ConfigureAwait(false);
            return;
        }
        if (ok && quiet)
            return;
        string rendered = CliRender.Render(method, res, text);
        if (ok)
            await stdout.WriteAsync(rendered).ConfigureAwait(false);
        else
            await stderr.WriteAsync(rendered).ConfigureAwait(false);
    }
}
