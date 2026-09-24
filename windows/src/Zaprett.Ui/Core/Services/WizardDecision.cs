using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Json;

namespace Zaprett.Ui.Core.Services;

/// <summary>
/// Whether the first-run wizard opens by itself. Only while the bypass has never been turned on: the installer with
/// AUTOSTART (or the command line) may already have turned it on, and then the person gets the home page. The lists
/// do not count: a clean install writes default lists into config.json without turning the bypass on (checked on
/// 2026-09-23 on Windows 10: enabled=false, running=false, three lists), and that person still needs the wizard.
/// "Passed" belongs to one installation of the service data (status.install_id, a GUID the core creates with a new
/// config.json and that goes away with REMOVEDATA): ui.json survives an uninstall in the user profile, and a clean
/// reinstall must show the wizard again (D11). A user without the rights to change the service (status.can_modify
/// false) never gets it: every step of it changes the service. Otherwise it stays available from the menu.
/// </summary>
public static class WizardDecision
{
    /// <summary>true — open the wizard; false — go home; null — not known yet (no status from the service).</summary>
    /// <param name="wizardDone">ui.json wizard_done.</param>
    /// <param name="wizardDoneFor">ui.json wizard_done_for: the installation the wizard was passed for (null in old files).</param>
    public static bool? ShouldOpen(bool wizardDone, string? wizardDoneFor, JsonObject? status)
    {
        if (status == null)
            return null;
        if (IsConfigured(status) || !ServiceAccess.CanModify(status))
            return false;
        // a service without install_id (older core): the flag of the user decides, as before
        if (InstallId(status) is not { } id)
            return !wizardDone;
        // an old ui.json (flag without the installation) counts as not passed for this one: the service is not set up
        return !string.Equals(wizardDoneFor, id, StringComparison.Ordinal);
    }

    /// <summary>Set up = the bypass is on or its engine is running.</summary>
    public static bool IsConfigured(JsonObject status) => status.Bool("enabled") || status.Bool("running");

    public static string? InstallId(JsonObject? status) => status.Str("install_id") is { Length: > 0 } id ? id : null;
}
