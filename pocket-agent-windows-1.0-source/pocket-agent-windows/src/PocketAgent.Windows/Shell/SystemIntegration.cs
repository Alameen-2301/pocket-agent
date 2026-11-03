using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PocketAgent.Core.Settings;
using PocketAgent.Windows.Tools;
using WinForms = System.Windows.Forms;

namespace PocketAgent.Windows.Shell;

/// <summary>API keys and the optional saved secret are encrypted with DPAPI for the current Windows user.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PocketAgent.Windows/secrets/v1");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public string Protect(string plain) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plain), protect: true));

    public string Unprotect(string protectedValue) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedValue), protect: false));

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DATA_BLOB { Size = data.Length, Data = input.AddrOfPinnedObject() };
            var entropyBlob = new DATA_BLOB { Size = Entropy.Length, Data = entropy.AddrOfPinnedObject() };
            var outBlob = new DATA_BLOB();
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Pocket Agent", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally { LocalFree(outBlob.Data); }
        }
        finally
        {
            input.Free();
            entropy.Free();
        }
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Pocket Agent";

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\" --minimized");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

internal static class ThemeHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19,
        DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch { return false; }
    }

    public static bool IsDark(string theme) => theme == "dark" || (theme != "light" && SystemPrefersDark());

    /// <summary>Colours the title bar to match the app (Windows 11; dark/light flag on Windows 10).</summary>
    public static void ApplyTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int flag = dark ? 1 : 0;
        if (Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref flag, sizeof(int)) != 0)
            Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref flag, sizeof(int));
        // COLORREF is 0x00BBGGRR. Matches --bg in the interface.
        int caption = dark ? 0x0020100E : 0x00FFF6F7;   // --bg   #0e1020 / #f7f6ff
        int text = dark ? 0x00FFEBEC : 0x003A1718;      // --text #ecebff / #18173a
        int border = dark ? 0x00522A26 : 0x00F3DCDF;    // --seam #262a52 / #dfdcf3
        Native.DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }
}

/// <summary>The notification-area icon: open, start/stop control, talk, quit; also shows notifications.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _control;

    public TrayIcon(System.Drawing.Icon icon, Action open, Action toggleControl, Action talk, Action quit, Func<bool> controlActive)
    {
        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("Open Pocket Agent", null, (_, _) => open()) { Font = new System.Drawing.Font(WinForms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
        _control = new WinForms.ToolStripMenuItem("Start desktop control", null, (_, _) => toggleControl());
        var talkItem = new WinForms.ToolStripMenuItem("Talk to the assistant", null, (_, _) => talk());
        var quitItem = new WinForms.ToolStripMenuItem("Quit Pocket Agent", null, (_, _) => quit());
        menu.Items.AddRange(new WinForms.ToolStripItem[] { openItem, _control, talkItem, new WinForms.ToolStripSeparator(), quitItem });
        menu.Opening += (_, _) => _control.Text = controlActive() ? "Stop desktop control" : "Start desktop control";
        _icon = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "Pocket Agent",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => open();
    }

    public void Notify(string title, string text)
    {
        try { _icon.ShowBalloonTip(5000, title, text.Length > 240 ? text[..240] + "…" : text, WinForms.ToolTipIcon.None); }
        catch { }
    }

    public void SetStatus(string text)
    {
        var t = "Pocket Agent — " + text;
        _icon.Text = t.Length > 63 ? t[..63] : t;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
