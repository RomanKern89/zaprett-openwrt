// compiled by zaprett-setup (net48, nullable off) and linked into tests/Zaprett.Core.Tests (nullable on)
#nullable disable
using System;
using System.Globalization;

namespace Zaprett.Setup
{
    /// <summary>Pure logic of zaprett-setup.exe (no I/O), shared with the unit tests (tests/Zaprett.Core.Tests).</summary>
    public static class SetupLogic
    {
        /// <summary>First argument of the elevated copy of the bootstrapper; never passed on to msiexec.</summary>
        public const string ElevatedMarker = "--zaprett-elevated";

        /// <summary>ERROR_CANCELLED: the UAC prompt was declined, closed or timed out.</summary>
        public const int ErrorCancelled = 1223;

        /// <summary>ERROR_INSTALL_USEREXIT: what the bootstrapper returns when the user gives up at the UAC step.</summary>
        public const int InstallUserExit = 1602;

        /// <summary>ERROR_INSTALL_FAILURE: the bootstrapper itself could not prepare the MSI.</summary>
        public const int InstallFailure = 1603;

        private static readonly char[] Blanks = { ' ', '\t' };

        /// <summary>
        /// The command line after the program name, exactly as typed (quotes kept), so that msiexec gets
        /// PROPERTY="value with spaces" in its own syntax: re-joining the split argv would move the quotes.
        /// The program name follows the CreateProcess rules: a quoted part up to the next quote, else up to whitespace.
        /// </summary>
        public static string RawArguments(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return string.Empty;
            int i = 0;
            if (commandLine[0] == '"')
            {
                int close = commandLine.IndexOf('"', 1);
                i = close < 0 ? commandLine.Length : close + 1;
            }
            while (i < commandLine.Length && !char.IsWhiteSpace(commandLine[i])) i++;
            return commandLine.Substring(i).Trim();
        }

        /// <summary>Removes the elevation marker (first token) from the raw arguments of the elevated copy.</summary>
        public static bool TryStripElevatedMarker(string rawArguments, out string rest)
        {
            rest = rawArguments ?? string.Empty;
            if (!rest.StartsWith(ElevatedMarker, StringComparison.Ordinal)) return false;
            string after = rest.Substring(ElevatedMarker.Length);
            if (after.Length > 0 && !char.IsWhiteSpace(after[0])) return false;
            rest = after.Trim();
            return true;
        }

        /// <summary>msiexec arguments: install the extracted package, then whatever the user added (/qn, /l*v, PROPS).</summary>
        public static string MsiexecArguments(string msiPath, string userArguments)
        {
            string args = "/i \"" + msiPath + "\"";
            string extra = (userArguments ?? string.Empty).Trim();
            return extra.Length == 0 ? args : args + " " + extra;
        }

        /// <summary>
        /// True when the user asked msiexec for a quiet or basic UI (/q, /qn, /qb, /qr, /passive, /quiet):
        /// the bootstrapper then shows no message boxes of its own either.
        /// </summary>
        public static bool IsQuiet(string rawArguments)
        {
            foreach (string token in Tokens(rawArguments))
            {
                string t = token.ToLowerInvariant();
                if (!(t.StartsWith("/", StringComparison.Ordinal) || t.StartsWith("-", StringComparison.Ordinal))) continue;
                string name = t.Substring(1);
                // /q = /qn; /qn, /qb, /qr with the optional + - ! modifiers; /qf is the full UI and is not quiet
                if (name == "passive" || name == "quiet" || name == "q" ||
                    name.StartsWith("qn", StringComparison.Ordinal) || name.StartsWith("qb", StringComparison.Ordinal) ||
                    name.StartsWith("qr", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Splits the raw arguments at blanks outside double quotes, so that a property value such as
        /// PROP="a /qn b" stays one token and is not taken for a switch.
        /// </summary>
        public static string[] Tokens(string rawArguments)
        {
            var tokens = new System.Collections.Generic.List<string>();
            var current = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char c in rawArguments ?? string.Empty)
            {
                if (c == '"') quoted = !quoted;
                if (!quoted && Array.IndexOf(Blanks, c) >= 0)
                {
                    if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens.ToArray();
        }

        /// <summary>msiexec results after which the product is installed: success, success with a restart pending.</summary>
        public static bool IsSuccess(int exitCode) => exitCode == 0 || exitCode == 3010 || exitCode == 1641;

        /// <summary>The elevated relaunch came back without administrator rights (a standard user, UAC switched off).</summary>
        public static string NotElevatedDetail(string lang) =>
            lang == "ru" ? "для установки нужны права администратора, а Windows запустила установщик без них"
            : lang == "zh" ? "安装需要管理员权限，但 Windows 在没有管理员权限的情况下启动了安装程序"
            : "administrator rights are required, and Windows started the setup without them";

        /// <summary>Language of the bootstrapper's own messages: the same three as the MSI (ru base, en, zh-CN).</summary>
        public static string LanguageOf(CultureInfo culture)
        {
            string two = culture?.TwoLetterISOLanguageName ?? "en";
            if (two == "ru") return "ru";
            if (two == "zh") return "zh";
            return "en";
        }

        public static string Title(string lang) =>
            lang == "ru" ? "Установка zaprett" : lang == "zh" ? "安装 zaprett" : "zaprett Setup";

        /// <summary>The UAC prompt was declined or closed: what happened and what to press next (Retry / Cancel).</summary>
        public static string ElevationDeclinedText(string lang)
        {
            if (lang == "ru")
                return "Установка не началась: Windows не получила разрешения на изменения.\n\n" +
                       "zaprett ставит службу и сетевой драйвер, поэтому нужны права администратора.\n\n" +
                       "Нажмите «Повтор» и в окне «Разрешить этому приложению вносить изменения на вашем устройстве?» " +
                       "выберите «Да». Если окно не появилось поверх остальных, щёлкните мигающий значок zaprett на панели задач.";
            if (lang == "zh")
                return "安装尚未开始：Windows 没有获得进行更改的许可。\n\n" +
                       "zaprett 需要安装服务和网络驱动程序，因此需要管理员权限。\n\n" +
                       "请点击“重试”，然后在“你要允许此应用对你的设备进行更改吗?”窗口中选择“是”。" +
                       "如果该窗口没有显示在最前面，请点击任务栏上闪烁的 zaprett 图标。";
            return "Setup has not started: Windows did not get permission to make changes.\n\n" +
                   "zaprett installs a service and a network driver, so administrator rights are needed.\n\n" +
                   "Click Retry and choose Yes in the \"Do you want to allow this app to make changes to your device?\" " +
                   "window. If that window is not on top, click the flashing zaprett icon on the taskbar.";
        }

        /// <summary>The bootstrapper could not unpack or check the embedded MSI.</summary>
        public static string PrepareFailedText(string lang, string detail)
        {
            if (lang == "ru")
                return "Не удалось подготовить установку zaprett:\n" + detail + "\n\n" +
                       "Скачайте установщик заново со страницы релизов или используйте файл .msi.";
            if (lang == "zh")
                return "无法准备 zaprett 安装：\n" + detail + "\n\n请从发布页面重新下载安装程序，或使用 .msi 文件。";
            return "Could not prepare the zaprett setup:\n" + detail + "\n\n" +
                   "Download the installer again from the releases page, or use the .msi file.";
        }
    }
}
