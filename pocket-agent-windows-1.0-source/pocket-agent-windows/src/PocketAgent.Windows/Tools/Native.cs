using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PocketAgent.Core.Safety;

namespace PocketAgent.Windows.Tools;

/// <summary>Win32 calls used by the tools, the overlays and the input monitor.</summary>
internal static class Native
{
    // ---------------------------------------------------------------- windows and processes
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll")] public static extern bool SwitchDesktop(IntPtr desktop);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_RESTORE = 9;
    public const uint GW_OWNER = 4, GA_ROOT = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public static string ProcessPath(int pid)
    {
        IntPtr handle = OpenProcess(0x1000, false, (uint)pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return "";
        try
        {
            var name = new StringBuilder(32768);
            int len = name.Capacity;
            return QueryFullProcessImageName(handle, 0, name, ref len) ? name.ToString() : "";
        }
        finally { CloseHandle(handle); }
    }

    public static string WindowTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(Math.Min(len + 1, 1024));
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static int ProcessIdOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return unchecked((int)pid);
    }

    /// <summary>Window bounds without the invisible resize border (DWM extended frame), in physical pixels.</summary>
    public static RECT WindowBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0) return r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    public static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static void AddExtendedStyle(IntPtr hwnd, long styles)
    {
        long current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(current | styles));
    }

    public static double DpiScaleAt(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT(x, y), 2); // MONITOR_DEFAULTTONEAREST
            if (GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1.0;
    }

    /// <summary>
    /// Brings a window to the front. Windows only lets the foreground process do this, so a
    /// synthetic Alt tap is sent first (the documented way to satisfy the foreground lock).
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (GetForegroundWindow() == hwnd) return true;
        // A no-op input event makes this process the last input source, which Windows requires before it
        // lets SetForegroundWindow succeed. (An Alt tap would also work but opens menus in many apps.)
        try { Send(MouseEvent(0)); } catch (Win32Exception) { }
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        for (int i = 0; i < 12 && GetForegroundWindow() != hwnd; i++) Thread.Sleep(50);
        if (GetForegroundWindow() == hwnd) return true;
        // Fallback: share input state with the current foreground thread for a moment.
        var foreground = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(foreground, out _);
        uint me = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true))
        {
            try
            {
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
            finally { AttachThreadInput(me, fgThread, false); }
        }
        for (int i = 0; i < 10 && GetForegroundWindow() != hwnd; i++) Thread.Sleep(50);
        return GetForegroundWindow() == hwnd;
    }

    // ---------------------------------------------------------------- input

    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint KeyExtended = 0x0001, KeyUp = 0x0002, KeyUnicode = 0x0004;
    public const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseRightDown = 0x0008, MouseRightUp = 0x0010,
        MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040, MouseWheel = 0x0800, MouseHWheel = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    private static void Send(params INPUT[] input)
    {
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<INPUT>()) != input.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows blocked the input (the app may be running as administrator).");
    }

    private static INPUT MouseEvent(uint flags, uint data = 0) => new()
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MOUSEINPUT { Flags = flags, Data = data } },
    };

    private static INPUT KeyEvent(ushort vk, bool up)
    {
        uint flags = (up ? KeyUp : 0) | (KeyChord.IsExtended(vk) ? KeyExtended : 0);
        return new INPUT
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Scan = (ushort)MapVirtualKey(vk, 0), Flags = flags } },
        };
    }

    private static INPUT UnicodeEvent(char c, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KEYBDINPUT { Vk = 0, Scan = c, Flags = KeyUnicode | (up ? KeyUp : 0) } },
    };

    public static void MoveTo(int x, int y)
    {
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Could not move the mouse pointer.");
    }

    public static void MouseButton(uint flags) => Send(MouseEvent(flags));

    public static void Wheel(bool horizontal, int delta) => Send(MouseEvent(horizontal ? MouseHWheel : MouseWheel, unchecked((uint)delta)));

    public static void KeyTap(ushort vk) => Send(KeyEvent(vk, false), KeyEvent(vk, true));

    /// <summary>Presses modifiers, taps the key, releases modifiers in reverse order.</summary>
    public static void Chord(IReadOnlyList<ushort> modifiers, ushort key)
    {
        var events = new List<INPUT>(modifiers.Count * 2 + 2);
        foreach (var m in modifiers) events.Add(KeyEvent(m, false));
        events.Add(KeyEvent(key, false));
        events.Add(KeyEvent(key, true));
        foreach (var m in modifiers.Reverse()) events.Add(KeyEvent(m, true));
        Send(events.ToArray());
    }

    public static void KeyCombo(params ushort[] keys)
    {
        if (keys.Length == 0) return;
        Chord(keys[..^1], keys[^1]);
    }

    /// <summary>
    /// Types text with Unicode key events. Newlines become Shift+Enter (a line break that does not
    /// send in chat apps) and tabs become the Tab key.
    /// </summary>
    public static void UnicodeType(string text, Func<bool> allowed)
    {
        var batch = new List<INPUT>(160);
        void Flush()
        {
            if (batch.Count == 0) return;
            if (!allowed()) throw new OperationCanceledException("Typing stopped.");
            Send(batch.ToArray());
            batch.Clear();
        }
        foreach (char c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n')
            {
                Flush();
                if (!allowed()) throw new OperationCanceledException("Typing stopped.");
                Chord([KeyChord.VK_SHIFT], 0x0D);
                continue;
            }
            if (c == '\t')
            {
                Flush();
                KeyTap(0x09);
                continue;
            }
            batch.Add(UnicodeEvent(c, false));
            batch.Add(UnicodeEvent(c, true));
            if (batch.Count >= 160) Flush();
        }
        Flush();
    }

    public static bool InteractiveDesktop()
    {
        const uint DesktopSwitch = 0x0100;
        var desktop = OpenInputDesktop(0, false, DesktopSwitch);
        if (desktop == IntPtr.Zero) return false;
        try { return SwitchDesktop(desktop); }
        finally { CloseDesktop(desktop); }
    }
}
