using System.ComponentModel;
using System.Runtime.InteropServices;
using Zaprett.Ui.Core.Json;
using Zaprett.Ui.Core.Loc;
using Zaprett.Ui.Core.Services;
using Zaprett.Ui.Core.ViewModels;

namespace Zaprett.Ui.Platform;

/// <summary>
/// Icon in the notification area (Shell_NotifyIcon on a hidden top-level window of the UI thread (message-only windows miss the "TaskbarCreated" broadcast)): colour by state
/// (on / off / warn / error), left click opens the window, right click shows the menu
/// "Turn on/off · Check now · Open · Exit interface · Close zaprett (bypass off, then exit)". Re-added when Explorer restarts ("TaskbarCreated").
/// </summary>
public sealed partial class TrayIcon : IDisposable, INotifyArea
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;
    private const int WmContextMenu = 0x007B;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifState = 0x8, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NisHidden = 0x1;
    private const uint NiifInfo = 0x1;
    private const int CmdToggle = 1, CmdCheck = 2, CmdOpen = 3, CmdExit = 4, CmdQuit = 5;
    private const uint WmTimer = 0x0113;
    private const uint RetryMs = 3000;
    private static readonly IntPtr RetryTimer = 1;

    /// <summary>One more modify with the image shortly after the icon was added: on Windows 10 an icon added right
    /// after the installer closed was kept by the shell but stayed invisible until its first modify (2026-09-24).</summary>
    private const uint ReshowMs = 2000;
    private static readonly IntPtr ReshowTimer = 2;

    private readonly MainWindow _window;
    private readonly AppState _state;
    private readonly ShellViewModel _shell;
    private readonly WndProc _proc;
    private readonly uint _taskbarCreated;
    private readonly Dictionary<string, IntPtr> _icons = [];
    private readonly TrayRegistration _registration;
    private IntPtr _hwnd;
    private string _iconState = "off";

    public TrayIcon(MainWindow window, AppState state)
    {
        _window = window;
        _state = state;
        _shell = App.Get<ShellViewModel>();
        _proc = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _registration = new TrayRegistration(this, App.Log);
    }

    public bool IsAdded => _registration.IsAdded;

    public void Create()
    {
        var cls = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "zaprett-tray-" + Environment.ProcessId,
        };
        if (RegisterClassEx(ref cls) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _hwnd = CreateWindowEx(0, cls.lpszClassName, "zaprett tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        int cx = GetSystemMetrics(49), cy = GetSystemMetrics(50);
        foreach (var s in TrayImages.States)
        {
            _icons[s] = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", $"tray-{s}.ico"), 1, cx, cy, 0x10);
            if (_icons[s] == IntPtr.Zero)
                App.Log($"tray: image {s} not loaded ({cx}x{cy}), error {Marshal.GetLastWin32Error()}");
        }
        _registration.Create();
        _shell.PropertyChanged += OnShellChanged;
        if (!_registration.IsAdded)
            SetTimer(_hwnd, RetryTimer, RetryMs, IntPtr.Zero);
        else
            AfterAdded();
    }

    /// <summary>Writes where the shell put the icon (a check for the next test run) and schedules the second modify.</summary>
    private void AfterAdded()
    {
        var id = new NotifyIconIdentifier { cbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), hWnd = _hwnd, uID = 1 };
        var hr = ShellNotifyIconGetRect(ref id, out var rect);
        App.Log(hr == 0
            ? $"tray: shown in state {_iconState} at {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}"
            : $"tray: no place in the notification area yet (state {_iconState}, 0x{hr:X8})");
        SetTimer(_hwnd, ReshowTimer, ReshowMs, IntPtr.Zero);
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.TrayState) or nameof(ShellViewModel.TrayTip))
            Update();
    }

    private NotifyIconData Data(uint flags)
    {
        _iconState = _shell.TrayState;
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = TrayImages.Pick(_iconState, _icons),
            // visible: never NIS_HIDDEN (only with NIF_STATE the shell reads these two)
            dwState = 0,
            dwStateMask = NisHidden,
            szTip = Truncate(_shell.TrayTip, 127),
            szInfo = "",
            szInfoTitle = "",
            uVersion = 4,
        };
    }

    // ---------- INotifyArea: the only calls of Shell_NotifyIcon for the icon itself (TrayRegistration decides) ----------

    bool INotifyArea.Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip | NifState);
        return ShellNotifyIcon(NimAdd, ref data);
    }

    bool INotifyArea.SetVersion()
    {
        var data = Data(0);
        return ShellNotifyIcon(NimSetVersion, ref data);
    }

    bool INotifyArea.Modify()
    {
        var before = _iconState;
        var data = Data(NifIcon | NifTip | NifShowTip | NifState);
        var ok = ShellNotifyIcon(NimModify, ref data);
        if (before != _iconState)
            App.Log($"tray: state {before} -> {_iconState}, modify={ok}");
        return ok;
    }

    bool INotifyArea.Delete()
    {
        var data = Data(0);
        return ShellNotifyIcon(NimDelete, ref data);
    }

    public void Update() => _registration.Update();

    public void UpdateMenuTexts() => Update();

    public void ShowBalloon(string title, string text)
    {
        if (!_registration.IsAdded)
            return;
        var data = Data(NifInfo);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NiifInfo;
        ShellNotifyIcon(NimModify, ref data);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            var ev = (int)(lParam.ToInt64() & 0xFFFF);
            // version 4 also sends WM_LBUTTONUP/WM_RBUTTONUP; only the high-level messages are handled, once each
            if (ev is NinSelect or NinKeySelect)
                _window.ShowFromTray();
            else if (ev is WmContextMenu)
                ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == WmTimer && wParam == RetryTimer)
        {
            _registration.Retry();
            if (_registration.IsAdded || _registration.IsDisposed)
            {
                KillTimer(hwnd, RetryTimer);
                if (_registration.IsAdded)
                    AfterAdded();
            }
            return IntPtr.Zero;
        }
        if (msg == WmTimer && wParam == ReshowTimer)
        {
            KillTimer(hwnd, ReshowTimer);
            _registration.Update();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _registration.OnTaskbarCreated();
            if (_registration.IsAdded)
                AfterAdded();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var running = _state.Status.Bool("running");
        var available = _state.IsAvailable;
        var menu = CreatePopupMenu();
        const uint grayed = 0x1, separator = 0x800;
        AppendMenu(menu, available ? 0u : grayed, CmdToggle, running ? L.T("Tray.TurnOff") : L.T("Tray.TurnOn"));
        AppendMenu(menu, available && running ? 0u : grayed, CmdCheck, L.T("Tray.CheckNow"));
        AppendMenu(menu, separator, 0, null);
        AppendMenu(menu, 0, CmdOpen, L.T("Tray.Open"));
        AppendMenu(menu, 0, CmdExit, L.T("Tray.Exit"));
        var quit = TrayQuit.Decide(available, running, _state.CanModify);
        AppendMenu(menu, quit == TrayQuitAction.NotAllowed ? grayed : 0u, CmdQuit, L.T("Tray.Quit"));
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0020 /* TPM_BOTTOMALIGN */, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        _ = RunCommandAsync(cmd);
    }

    private async Task RunCommandAsync(int cmd)
    {
        try
        {
            switch (cmd)
            {
                case CmdToggle:
                    await _state.CallAsync(_state.Status.Bool("running") ? "stop" : "start");
                    await _state.RefreshAsync();
                    break;
                case CmdCheck:
                    var (_, job) = await _state.RunJobAsync("probe", null, null);
                    _state.Probe = (await _state.CallAsync("probe.status")).Obj("probe");
                    var probe = _state.Probe;
                    _window.ShowBalloon(L.T("Tray.CheckDone"), L.F("Tray.CheckResult", probe.Long("ok"), probe.Long("total")));
                    await _state.RefreshAsync();
                    break;
                case CmdOpen:
                    _window.ShowFromTray();
                    break;
                case CmdExit:
                    _window.ExitFromTray();
                    break;
                case CmdQuit:
                    // the state is read again: the menu may have stayed open while the bypass changed
                    var action = TrayQuit.Decide(_state.IsAvailable, _state.Status.Bool("running"), _state.CanModify);
                    if (action == TrayQuitAction.NotAllowed)
                        break;
                    if (action == TrayQuitAction.StopThenExit)
                        await _state.CallAsync("stop"); // a failure ends in the catch below: the balloon says why, nothing closes
                    _window.ExitFromTray();
                    break;
            }
        }
        catch (ZaprettCallException e)
        {
            _window.ShowBalloon("zaprett", e.Text);
        }
    }

    public void Dispose()
    {
        _shell.PropertyChanged -= OnShellChanged;
        if (_hwnd != IntPtr.Zero)
        {
            KillTimer(_hwnd, RetryTimer);
            KillTimer(_hwnd, ReshowTimer);
        }
        _registration.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        foreach (var h in _icons.Values.Where(h => h != IntPtr.Zero))
            DestroyIcon(h);
        _icons.Clear();
    }

    // ---------- Win32 ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint elapse, IntPtr proc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr id);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

#pragma warning disable SYSLIB1054 // classic P/Invoke keeps the marshalling of the structs above simple
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier id, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
#pragma warning restore SYSLIB1054
}
