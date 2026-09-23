using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zaprett.Ui.Core.Services;

/// <summary>Preferences of the interface itself (not of the service): language, theme, first-run wizard.
/// Stored per user in %LOCALAPPDATA%\zaprett\ui.json.</summary>
public sealed class UiPrefs
{
    /// <summary>Last known language (ru, en, zh-CN): used before the service answers; the service keeps the
    /// authoritative ui.language in config.json.</summary>
    [JsonPropertyName("language")] public string Language { get; set; } = "ru";
    [JsonPropertyName("theme")] public string Theme { get; set; } = "system";
    [JsonPropertyName("wizard_done")] public bool WizardDone { get; set; }
    [JsonPropertyName("notifications")] public bool Notifications { get; set; } = true;
    [JsonPropertyName("close_to_tray")] public bool CloseToTray { get; set; } = true;

    /// <summary>Position and size of the window in the last session (physical pixels); checked against the current
    /// monitors before use (<see cref="WindowPlacement"/>).</summary>
    [JsonPropertyName("window")] public SavedWindow? Window { get; set; }

    [JsonIgnore] public string? Path { get; private set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zaprett", "ui.json");

    /// <summary>Reads the file; a missing or damaged file gives the defaults.</summary>
    public static UiPrefs Load(string? path)
    {
        UiPrefs prefs;
        try
        {
            prefs = path != null && File.Exists(path)
                ? JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(path), Options) ?? new UiPrefs()
                : new UiPrefs();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            prefs = new UiPrefs();
        }
        prefs.Path = path;
        prefs.Language = Loc.L.Normalize(prefs.Language);
        if (prefs.Theme is not ("system" or "light" or "dark"))
            prefs.Theme = "system";
        return prefs;
    }

    /// <summary>Atomic write (tmp + move); a failure is ignored: preferences are a convenience.</summary>
    public void Save()
    {
        if (Path == null)
            return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // not fatal: the next start uses defaults
        }
    }
}

public sealed class SavedWindow
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    public PixelRect ToRect() => new(X, Y, Width, Height);

    public static SavedWindow From(PixelRect r) => new() { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
}
