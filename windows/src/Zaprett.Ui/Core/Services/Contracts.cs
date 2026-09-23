using System.Text.Json.Nodes;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Text;

namespace Zaprett.Ui.Core.Services;

/// <summary>An answer {ok:false, error, message} of the service, or a transport failure (code service_unavailable).</summary>
public sealed class ZaprettCallException(string code, string? serviceMessage, JsonObject? reply = null, Exception? inner = null)
    : Exception(serviceMessage ?? code, inner)
{
    public string Code { get; } = code;
    public string? ServiceMessage { get; } = serviceMessage;
    public JsonObject? Reply { get; } = reply;

    /// <summary>Text for the person, in the interface language.</summary>
    public string Text => UiText.Error(Code, ServiceMessage);

    public IReadOnlyList<string> LineErrors => UiText.LineErrors(Reply);

    public static ZaprettCallException From(JsonObject reply) =>
        new(reply.Str("error") ?? "bad_answer", reply.Str("message"), reply);
}

public enum ConnectionState
{
    Connecting,
    Available,
    Unavailable,
}

/// <summary>Things the view models need from the desktop, implemented by the WinUI layer (and by fakes in tests).</summary>
public interface IUiPlatform
{
    /// <summary>Starts the "zaprett" Windows service with elevation (UAC). False when the user declined or it failed.</summary>
    Task<bool> StartServiceElevatedAsync();

    void CopyText(string text);

    /// <summary>Asks where to save and writes the text; returns the path or null when cancelled.</summary>
    Task<string?> SaveTextAsync(string suggestedName, string text);

    /// <summary>Asks for a text file and returns its content, or null when cancelled.</summary>
    Task<string?> OpenTextAsync();

    /// <summary>Windows notification (toast) or, if unavailable, a tray balloon.</summary>
    void Notify(string title, string text);

    Task<bool> ConfirmAsync(string title, string text, string primary);

    /// <summary>Applies interface preferences at once (theme; language rebuilds the pages).</summary>
    void ApplyTheme(string theme);

    void ApplyLanguage(string language);
}

/// <summary>Navigation between pages, implemented by the shell.</summary>
public interface INavigator
{
    void Navigate(string page);
}
