using System;
using System.Threading;
using System.Windows.Threading;
using PocketAgent.Core.App;

namespace PocketAgent.Windows.Overlay;

/// <summary>The bubble and the click ring, marshalled onto the UI thread.</summary>
internal sealed class OverlayService : IOverlay, IDisposable
{
    private readonly Dispatcher _ui;
    private BubbleWindow _bubble = null!;
    private ClickRing _ring = null!;

    public event Action<string>? Command;
    /// <summary>The user dragged the bubble: (centre X, bottom) in device-independent units.</summary>
    public event Action<double, double>? Moved;

    public OverlayService(Dispatcher ui, double? anchorX, double? anchorBottom)
    {
        _ui = ui;
        _ui.Invoke(() =>
        {
            _bubble = new BubbleWindow(anchorX, anchorBottom);
            _bubble.Command += c => Command?.Invoke(c);
            _bubble.Moved += (x, y) => Moved?.Invoke(x, y);
            _ring = new ClickRing();
        });
    }

    public void Show(OverlayView view) => _ui.BeginInvoke(() => _bubble.Apply(view));

    public void SetLevel(double level) => _ui.BeginInvoke(DispatcherPriority.Render, () => _bubble.SetLevel(level));

    public void PreviewClick(int x, int y, int durationMs)
    {
        _ui.BeginInvoke(() => _ring.ShowAt(x, y, durationMs));
        Thread.Sleep(durationMs);
    }

    public bool Covers(int x, int y) => _ui.Invoke(() => _bubble.ContainsScreenPoint(x, y));

    public void HideFor(int milliseconds) => _ui.Invoke(() => _bubble.HideFor(milliseconds));

    /// <summary>
    /// Runs a screen capture without Pocket Agent's floating windows in it. On Windows 10 2004+ they are
    /// excluded from capture already; on older builds they are hidden for a moment.
    /// </summary>
    public T CaptureWithoutOverlays<T>(Func<T> capture)
    {
        bool excluded = _ui.Invoke(() => _bubble.ExcludedFromCapture && _ring.ExcludedFromCapture);
        if (excluded) return capture();
        _ui.Invoke(() => { _bubble.HideFor(400); _ring.Hide(); });
        Thread.Sleep(80); // let the compositor drop them
        return capture();
    }

    public void Dispose()
    {
        try
        {
            _ui.Invoke(() =>
            {
                _bubble.Close();
                _ring.Dispose();
            });
        }
        catch { }
    }
}
