using System.Text.Json.Nodes;

namespace Zaprett.Service.Ipc;

/// <summary>
/// The few answers the service gives by itself (before the core), in the three languages of ARCHITECTURE-WIN §12.2.
/// The language is the request's "lang" (ru, en, zh-CN), Russian otherwise. The journal stays in English.
/// </summary>
public static class ServiceMessages
{
    private static readonly Dictionary<string, Dictionary<string, string>> Texts = new(StringComparer.Ordinal)
    {
        ["access_denied"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ru"] = "Для этого действия нужны права администратора (с повышением) или членство в группе «zaprett Operators»",
            ["en"] = "This action needs administrator rights (elevated) or membership in \"zaprett Operators\"",
            ["zh-CN"] = "此操作需要管理员权限（已提升）或“zaprett Operators”组成员身份",
        },
        ["too_many_subscriptions"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ru"] = "Слишком много подписок на события у этого пользователя",
            ["en"] = "Too many event subscriptions for this user",
            ["zh-CN"] = "该用户的事件订阅过多",
        },
    };

    public static IEnumerable<string> Codes => Texts.Keys;

    public static string Text(string code, string? lang)
    {
        var t = Texts[code];
        return lang is not null && t.TryGetValue(lang, out var s) ? s : t["ru"];
    }

    public static string? Lang(JsonObject? args) => args?["lang"] is JsonValue v && v.TryGetValue<string>(out var l) ? l : null;

    public static JsonObject Fail(string code, JsonObject? args) =>
        new() { ["ok"] = false, ["error"] = code, ["message"] = Text(code, Lang(args)) };
}
