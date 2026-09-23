using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Json;

namespace Zaprett.Ui.Core.Services;

/// <summary>
/// Whether the first-run wizard opens by itself. Only while the bypass has never been turned on: the installer with
/// AUTOSTART (or the command line) may already have turned it on, and then the person gets the home page. The lists
/// do not count: a clean install writes default lists into config.json without turning the bypass on (checked on
/// 2026-09-23 on Windows 10: enabled=false, running=false, three lists), and that person still needs the wizard.
/// The wizard stays available from the menu in any case.
/// </summary>
public static class WizardDecision
{
    /// <summary>true — open the wizard; false — go home; null — not known yet (no status from the service).</summary>
    public static bool? ShouldOpen(bool wizardDone, JsonObject? status)
    {
        if (wizardDone)
            return false;
        if (status == null)
            return null;
        return !IsConfigured(status);
    }

    /// <summary>Set up = the bypass is on or its engine is running.</summary>
    public static bool IsConfigured(JsonObject status) => status.Bool("enabled") || status.Bool("running");
}
