using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using PocketAgent.Windows.Tools;
using WinForms = System.Windows.Forms;

namespace PocketAgent.Windows.Overlay;

/// <summary>
/// The click preview: a glowing ring drawn at the exact physical pixel where the assistant is about
/// to click, a split second before it clicks. A per-pixel-alpha layered window that is click-through,
/// never activates and is excluded from screenshots.
/// </summary>
internal sealed class ClickRing : WinForms.NativeWindow, IDisposable
{
    private readonly DispatcherTimer _timer;
    private int _frame, _frames, _x, _y, _size;
    private double _scale = 1;

    public bool ExcludedFromCapture { get; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Native.POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref Native.POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    public ClickRing()
    {
        var cp = new WinForms.CreateParams
        {
            Caption = "",
            X = 0, Y = 0, Width = 1, Height = 1,
            Style = unchecked((int)0x80000000), // WS_POPUP
            ExStyle = (int)(Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
        };
        CreateHandle(cp);
        ExcludedFromCapture = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Step();
    }

    public void ShowAt(int x, int y, int durationMs)
    {
        _scale = Native.DpiScaleAt(x, y);
        _size = (int)Math.Round(72 * _scale);
        _x = x - _size / 2;
        _y = y - _size / 2;
        _frames = Math.Max(8, durationMs / 16);
        _frame = 0;
        Render(0);
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
        _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        Native.ShowWindow(Handle, Native.SW_HIDE);
    }

    private void Step()
    {
        _frame++;
        if (_frame > _frames + 6) { Hide(); return; }
        Render(Math.Min(1.0, _frame / (double)_frames));
    }

    private void Render(double t)
    {
        double ease = 1 - Math.Pow(1 - t, 3);
        float radius = (float)(_size * (0.44 - 0.26 * ease));
        int alpha = (int)(255 * (_frame > _frames ? Math.Max(0, 1 - (_frame - _frames) / 6.0) : 1));
        float stroke = (float)(3.2 * _scale);
        using var bmp = new Bitmap(_size, _size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float c = _size / 2f;
            using (var glow = new Pen(Color.FromArgb(alpha / 4, 0x7C, 0x6C, 0xFF), stroke * 3)) g.DrawEllipse(glow, c - radius, c - radius, radius * 2, radius * 2);   // --indigo
            using (var pen = new Pen(Color.FromArgb(alpha, 0x2F, 0xD6, 0xEE), stroke)) g.DrawEllipse(pen, c - radius, c - radius, radius * 2, radius * 2);            // --cyan
            float dot = (float)(4 * _scale);
            using var fill = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
            g.FillEllipse(fill, c - dot, c - dot, dot * 2, dot * 2);
        }
        Push(bmp);
    }

    private void Push(Bitmap bmp)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memory, hBitmap);
        try
        {
            var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
            var source = new Native.POINT(0, 0);
            var destination = new Native.POINT(_x, _y);
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref source, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(hBitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        DestroyHandle();
    }
}
