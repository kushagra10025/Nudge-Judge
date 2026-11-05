using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace NudgeJudge.Tray;

/// <summary>
/// The notification-area icon on Windows, straight on Win32: a hidden window receives the icon's mouse messages
/// and the popup menu is a native one, built fresh each time so its check mark is current.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsTray(TrayMenu menu) : ITrayIcon
{
    private readonly TrayMenu _menu = menu;

    private const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_APP = 0x8000;
    private const uint TrayCallback = WM_APP + 1;
    private const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint MF_STRING = 0, MF_CHECKED = 8, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    private const int SM_CXSMICON = 49;

    private enum Item { Open = 1, Routing, Rules, Activity, Quit }

    /// <summary>The window procedure is static; there's one tray icon per process.</summary>
    private static WindowsTray? s_current;

    private nint _window;
    private nint _icon;
    private uint _taskbarCreated;

    public void Run(CancellationToken stop)
    {
        s_current = this;
        // Crisp icon and menu at any scale (otherwise Windows stretches a 96-DPI bitmap of them).
        SetProcessDpiAwarenessContext(-4 /* PER_MONITOR_AWARE_V2 */);
        var instance = GetModuleHandleW(null);
        fixed (char* className = "NudgeJudgeTray")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            RegisterClassExW(&wc);
            // A hidden top-level window rather than a message-only one: the menu needs a window that can take focus.
            _window = CreateWindowExW(0, className, className, 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, instance, 0);
        }
        if (_window == 0)
        {
            Trace.TraceWarning($"NudgeJudge: couldn't create the tray window ({Marshal.GetLastPInvokeError()}).");
            stop.WaitHandle.WaitOne();
            return;
        }

        // Explorer restarting (or starting after us at login) drops every icon; it says so with this message.
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        var size = GetSystemMetrics(SM_CXSMICON);
        var (_, png) = TrayIconImage.Best(size);
        fixed (byte* bits = png)
            _icon = CreateIconFromResourceEx(bits, (uint)png.Length, 1, 0x00030000, size, size, 0);
        AddIcon();

        using var registration = stop.Register(() => PostMessageW(_window, WM_CLOSE, 0, 0));
        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    public void Invalidate()
    {
        // The menu is built when it opens, from the current setting.
    }

    public void Dispose()
    {
        var data = NewData();
        Shell_NotifyIconW(NIM_DELETE, &data);
        if (_icon != 0) DestroyIcon(_icon);
        _icon = 0;
    }

    private NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _window,
        uID = 1,
    };

    private void AddIcon()
    {
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = TrayCallback;
        data.hIcon = _icon;
        "NudgeJudge".AsSpan().CopyTo(new Span<char>(data.szTip, 128));
        if (Shell_NotifyIconW(NIM_ADD, &data) == 0)
            Trace.TraceWarning("NudgeJudge: the taskbar isn't ready for the tray icon yet; it's added once it is.");
    }

    private void ShowMenu()
    {
        GetCursorPos(out var point);
        var popup = CreatePopupMenu();
        AppendMenuW(popup, MF_STRING, (nuint)Item.Open, "Open NudgeJudge");
        SetMenuDefaultItem(popup, (uint)Item.Open, 0);
        AppendMenuW(popup, MF_SEPARATOR, 0, null);
        AppendMenuW(popup, MF_STRING | (_menu.RoutingEnabled() ? MF_CHECKED : 0), (nuint)Item.Routing, "Route links by rules");
        AppendMenuW(popup, MF_STRING, (nuint)Item.Rules, "Rules…");
        AppendMenuW(popup, MF_STRING, (nuint)Item.Activity, "Recent activity…");
        AppendMenuW(popup, MF_SEPARATOR, 0, null);
        AppendMenuW(popup, MF_STRING, (nuint)Item.Quit, "Quit NudgeJudge");

        // Without this the menu doesn't close when clicking elsewhere (and WM_NULL after it, per the docs).
        SetForegroundWindow(_window);
        var chosen = (Item)TrackPopupMenuEx(popup, TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD, point.X, point.Y, _window, 0);
        PostMessageW(_window, WM_NULL, 0, 0);
        DestroyMenu(popup);

        switch (chosen)
        {
            case Item.Open: _menu.Open(); break;
            case Item.Routing: _menu.ToggleRouting(); break;
            case Item.Rules: _menu.OpenRules(); break;
            case Item.Activity: _menu.OpenActivity(); break;
            case Item.Quit: _menu.Quit(); break;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (s_current is { } tray)
            {
                if (msg == TrayCallback)
                {
                    switch ((uint)lParam & 0xFFFF)
                    {
                        case WM_LBUTTONUP: tray._menu.Open(); return 0;
                        case WM_RBUTTONUP or WM_CONTEXTMENU: tray.ShowMenu(); return 0;
                    }
                    return 0;
                }
                if (msg == tray._taskbarCreated && msg != 0)
                {
                    tray.AddIcon();
                    return 0;
                }
            }
            switch (msg)
            {
                case WM_CLOSE: DestroyWindow(hwnd); return 0;
                case WM_DESTROY: PostQuitMessage(0); return 0;
            }
        }
        catch (Exception e)
        {
            // An exception can't cross back into Win32.
            Trace.TraceWarning($"NudgeJudge: tray message failed: {e.Message}");
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll")]
    private static partial int SetProcessDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WNDCLASSEXW* wc);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll")]
    private static partial int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconFromResourceEx(byte* bits, uint size, int isIcon, uint version, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint icon);

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);

    [LibraryImport("user32.dll")]
    private static partial int GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    private static partial int SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [LibraryImport("user32.dll")]
    private static partial int DestroyMenu(nint menu);
}
