using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Zaprett.Setup
{
    /// <summary>
    /// zaprett-setup.exe: asks for administrator rights FIRST, then runs the embedded MSI with its full UI.
    /// The plain MSI asks for elevation only after "Install", at the end of its pages; an unconfirmed prompt closes by
    /// itself after about two minutes and the setup ends as "interrupted" with nothing installed (ZERR-058), and the
    /// prompt sometimes waits behind a flashing taskbar button. Here the prompt comes right after the double click,
    /// and a declined one is explained with a Retry. Command-line arguments go to msiexec unchanged
    /// (zaprett-setup.exe /qn SERVICES=youtube /l*v setup.log); the exit code is msiexec's.
    /// </summary>
    internal static class Program
    {
        private const string MsiResource = "zaprett.msi";

        [STAThread]
        private static int Main()
        {
            // the display language, not the regional format: the message quotes the UAC window and names the message
            // box buttons, and Windows draws both in the display language (seen: English Windows with Russian regional
            // format shows "Retry / Cancel" and an English UAC window). The MSI pages follow the regional format
            // instead (Windows Installer picks the language transform by GetUserDefaultLangID), so on such a system
            // they may be in the other language - they speak for themselves.
            string lang = "en";
            bool quiet = false;
            try
            {
                lang = SetupLogic.LanguageOf(CultureInfo.CurrentUICulture);
                string raw = SetupLogic.RawArguments(Environment.CommandLine);
                quiet = SetupLogic.IsQuiet(raw);
                if (SetupLogic.TryStripElevatedMarker(raw, out string rest))
                {
                    // the relaunch did not elevate (a standard user with UAC switched off): nothing to do without rights
                    if (!IsElevated()) return Fail(lang, quiet, SetupLogic.NotElevatedDetail(lang));
                    return Install(rest, lang);
                }
                // already elevated ("Run as administrator", an elevated console, UAC off): no second prompt
                if (IsElevated()) return Install(raw, lang);
                return RelaunchElevated(raw, lang);
            }
            catch (Exception e)
            {
                // never a crash dialog with 0xE0434352 for a silent caller: every unexpected error is 1603
                return Fail(lang, quiet, e.Message);
            }
        }

        private static bool IsElevated()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static int RelaunchElevated(string raw, string lang)
        {
            bool quiet = SetupLogic.IsQuiet(raw);
            string self = Assembly.GetEntryAssembly().Location;
            while (true)
            {
                var psi = new ProcessStartInfo(self, (SetupLogic.ElevatedMarker + " " + raw).Trim())
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    // relative paths of the user's arguments (/l*v setup.log) stay relative to where they started it
                    WorkingDirectory = Environment.CurrentDirectory,
                };
                try
                {
                    using (Process p = Process.Start(psi))
                    {
                        if (p == null) return Fail(lang, quiet, "the elevated setup process did not start");
                        p.WaitForExit();
                        return p.ExitCode;
                    }
                }
                catch (Win32Exception e) when (e.NativeErrorCode == SetupLogic.ErrorCancelled)
                {
                    if (quiet) return SetupLogic.InstallUserExit;
                    if (!AskRetry(SetupLogic.ElevationDeclinedText(lang), SetupLogic.Title(lang)))
                        return SetupLogic.InstallUserExit;
                }
            }
        }

        /// <summary>
        /// Puts the MSI into the package cache %CommonProgramFiles%\zaprett\Installer\&lt;version&gt;\ and runs msiexec
        /// from there. Windows Installer records that folder as the source of the product, and a repair ("Repair" in
        /// Programs and Features, msiexec /f) takes the files from it: the cached copy in %WINDIR%\Installer has no
        /// files inside (EmbedCab), so a source in a deleted temp folder made a repair after an antivirus removed a file
        /// fail with 1603. Only SYSTEM and Administrators may write under Common Files, so the package cannot be swapped
        /// between the hash check and msiexec. The file is kept open read-only while msiexec runs (FileShare.Read lets
        /// Windows Installer read it; a handle with write access made it fail with 1619, ZERR-060).
        /// After a successful install the packages of other versions are deleted; a failed first install removes its
        /// own. A real removal of zaprett deletes the whole cache (installer-actions.ps1 -Action Uninstall).
        /// </summary>
        private static int Install(string userArgs, string lang)
        {
            bool quiet = SetupLogic.IsQuiet(userArgs);
            Assembly asm = Assembly.GetExecutingAssembly();
            string expected = Metadata(asm, "ZaprettMsiSha256");
            string name = Metadata(asm, "ZaprettMsiName");
            string version = Metadata(asm, "ZaprettMsiVersion");
            string productCode = Metadata(asm, "ZaprettProductCode");
            using (Stream res = asm.GetManifestResourceStream(MsiResource))
            {
                if (res == null || string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(name) ||
                    string.IsNullOrEmpty(version) || string.IsNullOrEmpty(productCode))
                    return Fail(lang, quiet, "the installer package is not embedded in this file");

                string root = CacheRoot();
                string dir = Path.Combine(root, version);
                string msi = Path.Combine(dir, name);
                Directory.CreateDirectory(dir);
                if (!File.Exists(msi) || !string.Equals(Sha256Of(msi), expected, StringComparison.OrdinalIgnoreCase))
                {
                    string part = msi + ".part";
                    using (var write = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        res.CopyTo(write, 1 << 20);
                        write.Flush(true);
                    }
                    if (File.Exists(msi)) File.Delete(msi);
                    File.Move(part, msi);
                }

                int code;
                using (var fs = new FileStream(msi, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string actual = Sha256Of(fs);
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        return Fail(lang, quiet, "SHA-256 of the unpacked package does not match (" + actual + ")");
                    code = RunMsiexec(SetupLogic.MsiexecArguments(msi, userArgs));
                }

                if (SetupLogic.IsSuccess(code)) PruneCache(root, version);
                else if (!IsProductInstalled(productCode)) { TryDelete(dir); TryDeleteIfEmpty(root); TryDeleteIfEmpty(Path.GetDirectoryName(root)); }
                return code;
            }
        }

        private static string CacheRoot() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "zaprett", "Installer");

        /// <summary>After a successful install or upgrade only the package of the installed version stays.</summary>
        private static void PruneCache(string root, string keepVersion)
        {
            foreach (string sub in Directory.GetDirectories(root))
                if (!string.Equals(Path.GetFileName(sub), keepVersion, StringComparison.OrdinalIgnoreCase))
                    TryDelete(sub);
        }

        private static int RunMsiexec(string arguments)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            using (Process p = Process.Start(psi))
            {
                if (p == null) throw new InvalidOperationException("msiexec did not start");
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        private static string Sha256Of(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return Sha256Of(fs);
        }

        private static string Sha256Of(Stream s)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(s).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        // INSTALLSTATE_DEFAULT: the product is installed for this machine
        private const int InstallStateDefault = 5;

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiQueryProductStateW(string productCode);

        private static bool IsProductInstalled(string productCode) =>
            MsiQueryProductStateW(productCode) == InstallStateDefault;

        // MessageBoxW flags: after a declined UAC prompt the foreground belongs to another window, and a plain message
        // box opened inactive behind it (seen on Windows 11: grey title, Enter went to the desktop). MB_SETFOREGROUND
        // asks for the foreground, MB_TOPMOST keeps it above other windows until it is answered.
        private const uint MbOk = 0x00000000;
        private const uint MbRetryCancel = 0x00000005;
        private const uint MbIconError = 0x00000010;
        private const uint MbIconWarning = 0x00000030;
        private const uint MbSetForeground = 0x00010000;
        private const uint MbTopMost = 0x00040000;
        private const int IdRetry = 4;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private static bool AskRetry(string text, string title) =>
            MessageBoxW(IntPtr.Zero, text, title, MbRetryCancel | MbIconWarning | MbSetForeground | MbTopMost) == IdRetry;

        private static string Metadata(Assembly asm, string key) =>
            asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

        private static int Fail(string lang, bool quiet, string detail)
        {
            if (!quiet)
                _ = MessageBoxW(IntPtr.Zero, SetupLogic.PrepareFailedText(lang, detail), SetupLogic.Title(lang),
                    MbOk | MbIconError | MbSetForeground | MbTopMost);
            return SetupLogic.InstallFailure;
        }

        private static void TryDelete(string dir)
        {
            for (int i = 0; i < 5 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, true); }
                catch (IOException) { System.Threading.Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(500); }
            }
        }

        private static void TryDeleteIfEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
