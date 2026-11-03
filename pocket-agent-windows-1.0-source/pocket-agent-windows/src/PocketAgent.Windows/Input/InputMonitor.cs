using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PocketAgent.Windows.Tools;

namespace PocketAgent.Windows.Input;

/// <summary>
/// Low-level keyboard and mouse hooks on their own thread. They tell real input from the input
/// Pocket Agent injects (the LLKHF_INJECTED / LLMHF_INJECTED flags), which gives:
///  • the push-to-talk key (works while any app is in front),
///  • instant takeover: the user's own mouse or keyboard pauses automation,
///  • the emergency stop: Esc three times quickly.
/// Callbacks only record what happened; events are raised in order on a separate worker.
/// </summary>
internal sealed class InputMonitor : IDisposable
{
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public Native.POINT Pt; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint VkCode, ScanCode, Flags, Time; public UIntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public Native.POINT Pt; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }

    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012;
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207,
        WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_MOUSEHWHEEL = 0x20E;
    private const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;
    private const uint VK_SPACE = 0x20, VK_ESCAPE = 0x1B, VK_F8 = 0x77, VK_F9 = 0x78, VK_PAUSE = 0x13,
        VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    private readonly int _ownPid;
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private readonly Channel<Action> _events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHook, _mouseHook;

    // Settings (changed from other threads).
    private volatile string _talkKey = "RightCtrl";
    private volatile bool _takeover = true;
    private volatile bool _tripleEsc = true;

    // Hook-thread state.
    private bool _lctrl, _rctrl, _lshift, _rshift, _lalt, _ralt;
    private bool _talkActive;
    private bool _rctrlHeld, _rctrlChorded;
    private int _rctrlGeneration;
    private readonly long[] _escTimes = new long[3];
    private int _escIndex;
    private bool _escDown;
    private Native.POINT _anchor;
    private long _anchorTime, _lastUserInput;

    public event Action<bool>? TalkKey;
    public event Action? UserInput;
    public event Action? Panic;

    public InputMonitor(int ownPid)
    {
        _ownPid = ownPid;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _ = Task.Run(async () =>
        {
            await foreach (var action in _events.Reader.ReadAllAsync())
            {
                try { action(); } catch (Exception e) { CrashLog.Write(e); }
            }
        });
    }

    public void Configure(string talkKey, bool takeover, bool tripleEsc)
    {
        lock (_stateLock)
        {
            if (_talkKey != talkKey && _talkActive)
            {
                _talkActive = false;
                Raise(() => TalkKey?.Invoke(false));
            }
            _talkKey = talkKey;
        }
        _takeover = takeover;
        _tripleEsc = tripleEsc;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Pocket Agent input hooks" };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            CrashLog.Info("Input hooks could not be installed: " + Marshal.GetLastWin32Error());
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
    }

    private void Raise(Action action) => _events.Writer.TryWrite(action);

    private static long Now => Environment.TickCount64;

    private bool ForegroundIsOwn() => Native.ProcessIdOf(Native.GetForegroundWindow()) == _ownPid;

    private bool PointIsOwn(Native.POINT p)
    {
        var hwnd = Native.WindowFromPoint(p);
        if (hwnd == IntPtr.Zero) return false;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        return Native.ProcessIdOf(root == IntPtr.Zero ? hwnd : root) == _ownPid;
    }

    private void ReportUserInput()
    {
        if (!_takeover) return;
        var now = Now;
        if (now - _lastUserInput < 400) return;
        _lastUserInput = now;
        Raise(() => UserInput?.Invoke());
    }

    // ---------------------------------------------------------------- keyboard

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool swallow = false;
                if ((k.Flags & LLKHF_INJECTED) == 0)
                    lock (_stateLock) swallow = HandleKey(k.VkCode, (int)wParam);
                if (swallow) return (IntPtr)1;
            }
            catch (Exception e) { Debug.WriteLine(e); }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>Returns true to swallow the key (only the talk key in its combination modes).</summary>
    private bool HandleKey(uint vk, int message)
    {
        bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool up = message is WM_KEYUP or WM_SYSKEYUP;
        if (!down && !up) return false;
        bool wasDown = vk switch
        {
            VK_LCONTROL => _lctrl, VK_RCONTROL => _rctrl, VK_LSHIFT => _lshift, VK_RSHIFT => _rshift,
            VK_LMENU => _lalt, VK_RMENU => _ralt, _ => false,
        };
        switch (vk)
        {
            case VK_LCONTROL: _lctrl = down; break;
            case VK_RCONTROL: _rctrl = down; break;
            case VK_LSHIFT: _lshift = down; break;
            case VK_RSHIFT: _rshift = down; break;
            case VK_LMENU: _lalt = down; break;
            case VK_RMENU: _ralt = down; break;
        }

        bool isTalkKey = false;
        bool swallow = false;
        switch (_talkKey)
        {
            case "RightCtrl":
                if (vk == VK_RCONTROL)
                {
                    isTalkKey = true;
                    if (down && !wasDown)
                    {
                        _rctrlHeld = true;
                        _rctrlChorded = false;
                        int generation = ++_rctrlGeneration;
                        // Start listening only if Right Ctrl is held on its own for a moment, so Ctrl shortcuts keep working.
                        _ = Task.Delay(220).ContinueWith(_ =>
                        {
                            lock (_stateLock)
                            {
                                if (_rctrlHeld && !_rctrlChorded && generation == _rctrlGeneration && !_talkActive && _talkKey == "RightCtrl")
                                {
                                    _talkActive = true;
                                    Raise(() => TalkKey?.Invoke(true));
                                }
                            }
                        }, TaskScheduler.Default);
                    }
                    else if (up)
                    {
                        _rctrlHeld = false;
                        if (_talkActive)
                        {
                            _talkActive = false;
                            Raise(() => TalkKey?.Invoke(false));
                        }
                    }
                }
                else if (down && _rctrlHeld)
                {
                    _rctrlChorded = true; // Right Ctrl is being used as a modifier
                }
                break;
            case "CtrlSpace":
            case "CtrlShiftSpace":
            case "CtrlAltSpace":
                if (vk == VK_SPACE)
                {
                    bool ctrl = _lctrl || _rctrl, shift = _lshift || _rshift;
                    bool mods = _talkKey switch
                    {
                        "CtrlSpace" => ctrl && !shift && !_lalt,
                        "CtrlShiftSpace" => ctrl && shift && !_lalt,
                        _ => ctrl && _lalt && !shift, // left Alt only, so AltGr+Space keeps working
                    };
                    if (down && (mods || _talkActive))
                    {
                        isTalkKey = true;
                        swallow = true;
                        if (!_talkActive)
                        {
                            _talkActive = true;
                            Raise(() => TalkKey?.Invoke(true));
                        }
                    }
                    else if (up && _talkActive)
                    {
                        isTalkKey = true;
                        swallow = true;
                        _talkActive = false;
                        Raise(() => TalkKey?.Invoke(false));
                    }
                }
                else if (_talkActive && vk is VK_LCONTROL or VK_RCONTROL or VK_LSHIFT or VK_RSHIFT or VK_LMENU)
                {
                    isTalkKey = true;
                }
                break;
            default:
                uint single = _talkKey switch { "F8" => VK_F8, "F9" => VK_F9, "Pause" => VK_PAUSE, _ => 0 };
                if (single != 0 && vk == single)
                {
                    isTalkKey = true;
                    swallow = true;
                    if (down && !_talkActive)
                    {
                        _talkActive = true;
                        Raise(() => TalkKey?.Invoke(true));
                    }
                    else if (up && _talkActive)
                    {
                        _talkActive = false;
                        Raise(() => TalkKey?.Invoke(false));
                    }
                }
                break;
        }

        // Count separate Esc presses only: holding Esc auto-repeats key-down messages.
        bool escPress = vk == VK_ESCAPE && down && !_escDown;
        if (vk == VK_ESCAPE) _escDown = down;
        if (escPress && _tripleEsc)
        {
            var now = Now;
            _escTimes[_escIndex % 3] = now;
            _escIndex++;
            if (_escIndex >= 3 && now - _escTimes[_escIndex % 3] < 1200)
            {
                _escIndex = 0;
                Array.Clear(_escTimes);
                Raise(() => Panic?.Invoke());
            }
        }

        if (down && !isTalkKey && !ForegroundIsOwn()) ReportUserInput();
        return swallow;
    }

    /// <summary>Guards the key state shared between the hook thread and the Right-Ctrl hold timer.</summary>
    private readonly object _stateLock = new();

    // ---------------------------------------------------------------- mouse

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((m.Flags & LLMHF_INJECTED) == 0)
                {
                    int message = (int)wParam;
                    // Ctrl+click and Ctrl+wheel use Right Ctrl as a modifier: don't start listening.
                    if (message != WM_MOUSEMOVE && _talkKey == "RightCtrl")
                        lock (_stateLock) { if (_rctrlHeld) _rctrlChorded = true; }
                    if (_takeover) HandleMouse(message, m.Pt);
                }
            }
            catch (Exception e) { Debug.WriteLine(e); }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void HandleMouse(int message, Native.POINT p)
    {
        if (message == WM_MOUSEMOVE)
        {
            var now = Now;
            if (now - _anchorTime > 500)
            {
                _anchor = p;
                _anchorTime = now;
                return;
            }
            int dx = p.X - _anchor.X, dy = p.Y - _anchor.Y;
            if (dx * dx + dy * dy > 26 * 26)
            {
                _anchor = p;
                _anchorTime = now;
                if (!PointIsOwn(p)) ReportUserInput();
            }
            return;
        }
        if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN or WM_MOUSEWHEEL or WM_MOUSEHWHEEL)
        {
            if (!PointIsOwn(p)) ReportUserInput();
        }
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
        _events.Writer.TryComplete();
    }
}
