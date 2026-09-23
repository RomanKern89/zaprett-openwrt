using System.Runtime.InteropServices;
using Zaprett.Ui.Core.Loc;

namespace Zaprett.Ui.Platform;

/// <summary>
/// Language of the built-in texts of WinUI controls (switch labels, context menus of text boxes). For an unpackaged
/// app these follow the MUI preferred UI languages of the process, not ApplicationLanguages: checked on 2026-09-23,
/// Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride throws without package identity, and the
/// Microsoft.Windows.Globalization one and CultureInfo.DefaultThreadCurrentUICulture change nothing.
/// Must be called before Application.Start; a later call affects only controls whose resources are not loaded yet.
/// </summary>
public static class ControlsLanguage
{
    private const uint MuiLanguageName = 0x8;

    public static void Apply(string language)
    {
        var culture = L.Normalize(language) switch
        {
            L.English => "en-US",
            L.Chinese => "zh-CN",
            _ => "ru-RU",
        };
        // a double-null-terminated list of names
        var list = culture + "\0\0";
        if (!SetProcessPreferredUILanguages(MuiLanguageName, list, out _))
            App.Log($"controls language: SetProcessPreferredUILanguages({culture}) failed, error {Marshal.GetLastWin32Error()}");
        if (!SetThreadPreferredUILanguages(MuiLanguageName, list, out _))
            App.Log($"controls language: SetThreadPreferredUILanguages({culture}) failed, error {Marshal.GetLastWin32Error()}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetProcessPreferredUILanguages(uint flags, string languages, out uint count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetThreadPreferredUILanguages(uint flags, string languages, out uint count);
}
