using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zaprett.Core;
using Zaprett.Core.Platform;
using Zaprett.Service.Ipc;
using Zaprett.Service.Platform;

namespace Zaprett.Service.Hosting;

/// <summary>
/// Methods the service answers itself, in front of the core: "tray.autostart" ({enable: bool} → {tray_autostart}, a
/// change: operators and admins only) and the field "tray_autostart" in "status" (read from the registry at the answer).
/// Everything else goes to the core unchanged.
/// </summary>
public sealed class ServiceMethods(ICommandDispatcher core, TrayAutostart tray, ILog log) : ICommandDispatcher
{
    public const string TrayAutostartMethod = "tray.autostart";

    public IReadOnlySet<string> ReadOnlyMethods => core.ReadOnlyMethods;

    public event Action<string, JsonObject>? Event
    {
        add => core.Event += value;
        remove => core.Event -= value;
    }

    public async Task<JsonObject> InvokeAsync(string method, JsonObject? args, CallerInfo caller, CancellationToken ct)
    {
        if (method == TrayAutostartMethod)
            return SetTrayAutostart(args, caller);
        var r = await core.InvokeAsync(method, args, caller, ct).ConfigureAwait(false);
        if (method == "status")
            AddTrayAutostart(r);
        // "page" carries the status of its page under "status" (the UI keeps its state from it, D18)
        else if (method == "page" && r["status"] is JsonObject pageStatus)
            AddTrayAutostart(pageStatus);
        return r;
    }

    private void AddTrayAutostart(JsonObject status)
    {
        if (status["ok"] is JsonValue ok && ok.TryGetValue(out bool isOk) && isOk)
            status["tray_autostart"] = tray.IsOn();
    }

    private JsonObject SetTrayAutostart(JsonObject? args, CallerInfo caller)
    {
        // the pipe server refuses it already (not a read-only method); checked here too for any other caller
        if (!caller.CanModify)
            return ServiceMessages.Fail("access_denied", args);
        if (args?["enable"] is not JsonValue v || v.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            return ServiceMessages.Fail("invalid_argument", args);
        bool enable = v.GetValue<bool>();
        try
        {
            bool now = tray.Set(enable);
            return new JsonObject { ["ok"] = true, ["tray_autostart"] = now };
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Warn($"tray autostart: {e.Message}");
            return ServiceMessages.Fail("tray_autostart_failed", args);
        }
    }
}
