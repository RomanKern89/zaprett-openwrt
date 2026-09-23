using System.Text.Json.Nodes;
using Microsoft.Win32;
using Zaprett.Core;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Hosting;

/// <summary>Options the MSI wrote to HKLM\SOFTWARE\zaprett (64-bit view, REG_SZ).</summary>
public sealed record InstallOptionValues(string? InstallDir, string? Version, string? AutoStart, string? Services);

/// <summary>
/// First start after installation: the MSI options AUTOSTART and SERVICES (winsetup) are applied once through the core
/// — <c>wizard.apply {services}</c>, then <c>enable</c> when AutoStart is "1" — and a marker file in the data directory
/// remembers that, so upgrades and later starts never override what the user changed since.
/// </summary>
public static class InstallOptions
{
    public const string RegistryKey = @"SOFTWARE\zaprett";
    public const string MarkerFile = "install-options.applied";

    public static InstallOptionValues? Read()
    {
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey(RegistryKey);
        if (k is null)
            return null;
        return new InstallOptionValues(k.GetValue("InstallDir") as string, k.GetValue("Version") as string,
            k.GetValue("AutoStart") as string, k.GetValue("Services") as string);
    }

    /// <summary>The calls for the options (pure, for tests).</summary>
    public static IReadOnlyList<(string Method, JsonObject? Args)> Calls(InstallOptionValues o)
    {
        var calls = new List<(string, JsonObject?)>();
        var services = (o.Services ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (services.Length > 0)
            calls.Add(("wizard.apply", new JsonObject { ["services"] = new JsonArray(services.Select(s => (JsonNode)s).ToArray()) }));
        if (o.AutoStart == "1")
            calls.Add(("enable", null));
        return calls;
    }

    public static async Task ApplyOnceAsync(ICommandDispatcher dispatcher, IPaths paths, ILog log, InstallOptionValues? options,
        CancellationToken ct)
    {
        string marker = Path.Combine(paths.DataDir, MarkerFile);
        if (options is null || File.Exists(marker))
            return;
        foreach (var (method, args) in Calls(options))
        {
            var r = await dispatcher.InvokeAsync(method, args, CallerInfo.System, ct).ConfigureAwait(false);
            if (r["ok"] is JsonValue v && v.TryGetValue<bool>(out var ok) && ok)
                log.Info($"install options: {method} done");
            else
                log.Warn($"install options: {method} failed: {r.ToJsonString()}");
        }
        // written even after a failed call: a broken option must not be retried (and fail) on every start
        await File.WriteAllTextAsync(marker, options.Version ?? "", ct).ConfigureAwait(false);
    }
}
