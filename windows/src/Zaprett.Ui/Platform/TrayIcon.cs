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
/// "Turn on/off · Check now · Open · Exit interface". Re-added when Explorer restarts ("TaskbarCreated").
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;
    private const int WmContextMenu = 0x007B;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifInfo = 0x1;
    private const int CmdToggle = 1, CmdCheck = 2, CmdOpen = 3, CmdExit = 4;

    private readonly MainWindow _window;
    private readonly AppState _state;
    private readonly ShellViewModel _shell;
    private readonly WndProc _proc;
    private readonly uint _taskbarCreated;
    private readonly Dictionary<string, IntPtr> _icons = [];
    private IntPtr _hwnd;
    private string _iconState = "off";
    private bool _added;

    public TrayIcon(MainWindow window, AppState state)
    {
        _window = window;
        _state = state;
        _shell = App.Get<ShellViewModel>();
        _proc = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    public bool IsAdded => _added;

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
        foreach (var s in new[] { "on", "off", "warn", "error" })
            _icons[s] = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", $"tray-{s}.ico"), 1,
                GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
        Add();
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.TrayState) or nameof(ShellViewModel.TrayTip))
                Update();
        };
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
            hIcon = _icons.GetValueOrDefault(_iconState, _icons.GetValueOrDefault("off")),
            szTip = Truncate(_shell.TrayTip, 127),
            szInfo = "",
            szInfoTitle = "",
            uVersion = 4,
        };
    }

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        _added = ShellNotifyIcon(NimAdd, ref data);
        if (_added)
        {
            ShellNotifyIcon(NimSetVersion, ref data);
            App.Log("tray: icon added, state " + _iconState);
        }
        else
            App.Log("tray: Shell_NotifyIcon(NIM_ADD) failed");
    }

    public void Update()
    {
        if (!_added)
            return;
        var before = _iconState;
        var data = Data(NifIcon | NifTip | NifShowTip);
        var ok = ShellNotifyIcon(NimModify, ref data);
        if (before != _iconState)
            App.Log($"tray: state {before} -> {_iconState}, modify={ok}");
    }

    public void UpdateMenuTexts() => Update();

    public void ShowBalloon(string title, string text)
    {
        if (!_added)
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
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            Add();
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
            }
        }
        catch (ZaprettCallException e)
        {
            _window.ShowBalloon("zaprett", e.Text);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            ShellNotifyIcon(NimDelete, ref data);
            _added = false;
        }
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
    private struct Point
    {
        public int X;
        public int Y;
    }

#pragma warning disable SYSLIB1054 // classic P/Invoke keeps the marshalling of the structs above simple
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

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
