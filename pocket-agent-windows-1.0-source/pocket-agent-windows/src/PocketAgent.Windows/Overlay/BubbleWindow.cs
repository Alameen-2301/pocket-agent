using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using PocketAgent.Core.App;
using PocketAgent.Windows.Tools;

namespace PocketAgent.Windows.Overlay;

/// <summary>
/// The floating voice bubble: an always-on-top pill that shows listening, thinking, the current
/// step, spoken captions and approval prompts. It never takes focus from the app being used and
/// is excluded from screenshots, so the assistant never sees (or clicks) it.
/// </summary>
internal sealed class BubbleWindow : Window
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly FontFamily Icons = new(IconFont);

    private readonly Border _shell;
    private readonly Ellipse _halo;
    private readonly ScaleTransform _haloScale = new(1, 1);
    private readonly Ellipse _orb;
    private readonly TextBlock _orbGlyph;
    private readonly TextBlock _title;
    private readonly TextBlock _caption;
    private readonly StackPanel _buttons;
    private readonly WrapPanel _choices;
    private readonly DispatcherTimer _unhide;
    private OverlayView _view = new();
    private DateTime _hiddenUntil = DateTime.MinValue;
    private double _anchorX, _anchorBottom;
    private double _level;
    private string _animatedMode = "";

    public event Action<string>? Command;
    public event Action<double, double>? Moved;
    public bool ExcludedFromCapture { get; private set; }

    public BubbleWindow(double? anchorX, double? anchorBottom)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Pocket Agent";
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;

        _halo = new Ellipse
        {
            Width = 38, Height = 38,
            Fill = new SolidColorBrush(Color.FromArgb(90, 0x2F, 0xD6, 0xEE)),   // cyan halo follows your voice
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _haloScale,
            Opacity = 0,
        };
        _orb = new Ellipse { Width = 30, Height = 30, Fill = Gradient("#7C6CFF", "#2FD6EE") };
        _orbGlyph = new TextBlock
        {
            FontFamily = Icons, FontSize = 13, Foreground = Brushes.White, Text = "",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var orbHost = new Grid { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
        orbHost.Children.Add(_halo);
        orbHost.Children.Add(_orb);
        orbHost.Children.Add(_orbGlyph);
        orbHost.MouseLeftButtonDown += (_, e) => e.Handled = true;
        orbHost.MouseLeftButtonUp += (_, e) => { e.Handled = true; Raise(_view.Mode is "prompt" ? "open" : "talk"); };
        orbHost.ToolTip = "Talk";

        _title = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEB, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340,
        };
        _caption = new TextBlock
        {
            FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xA5, 0xA8, 0xCE)),
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340, MaxHeight = 34,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0), MinWidth = 120 };
        text.Children.Add(_title);
        text.Children.Add(_caption);

        _buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(orbHost, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_buttons, 2);
        row.Children.Add(orbHost);
        row.Children.Add(text);
        row.Children.Add(_buttons);

        _choices = new WrapPanel { Margin = new Thickness(44, 6, 4, 2), Visibility = Visibility.Collapsed, MaxWidth = 460 };

        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(_choices);

        _shell = new Border
        {
            CornerRadius = new CornerRadius(26),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x15, 0x18, 0x34)),   // --hull
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2A, 0x52)),         // --seam
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 6, 8, 6),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Color.FromRgb(0x03, 0x04, 0x12) },
            Cursor = Cursors.SizeAll,
        };
        _shell.MouseLeftButtonDown += OnDragStart;

        Content = new Grid { Margin = new Thickness(18), Children = { _shell } };

        var work = SystemParameters.WorkArea;
        _anchorX = work.Left + work.Width / 2;
        _anchorBottom = work.Bottom - 6;
        if (anchorX is double ax && anchorBottom is double ab && SystemParameters.VirtualScreenLeft <= ax
            && ax <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && SystemParameters.VirtualScreenTop <= ab && ab <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 40)
        {
            _anchorX = ax;
            _anchorBottom = ab;
        }

        _unhide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _unhide.Tick += (_, _) =>
        {
            if (DateTime.UtcNow < _hiddenUntil) return;
            _unhide.Stop();
            Apply(_view);
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExtendedStyle(hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);
            ExcludedFromCapture = Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        SizeChanged += (_, _) => Reposition();
        Apply(_view);
    }

    private static LinearGradientBrush Gradient(string from, string to)
    {
        var a = (Color)ColorConverter.ConvertFromString(from);
        var b = (Color)ColorConverter.ConvertFromString(to);
        return new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1));
    }

    private void Raise(string command) => Command?.Invoke(command);

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch (InvalidOperationException) { return; }
        _anchorX = Left + ActualWidth / 2;
        _anchorBottom = Top + ActualHeight;
        Moved?.Invoke(_anchorX, _anchorBottom);
    }

    private void Reposition()
    {
        if (!IsVisible) return;
        Left = _anchorX - ActualWidth / 2;
        Top = _anchorBottom - ActualHeight;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (Left < vs.Left) Left = vs.Left;
        if (Left + ActualWidth > vs.Right) Left = vs.Right - ActualWidth;
        if (Top < vs.Top) Top = vs.Top;
    }

    /// <summary>Physical-pixel hit test against the visible pill (not the transparent shadow margin).</summary>
    public bool ContainsScreenPoint(int x, int y)
    {
        if (!IsVisible || _shell.ActualWidth <= 0) return false;
        try
        {
            var a = _shell.PointToScreen(new Point(0, 0));
            var b = _shell.PointToScreen(new Point(_shell.ActualWidth, _shell.ActualHeight));
            return x >= a.X - 4 && x <= b.X + 4 && y >= a.Y - 4 && y <= b.Y + 4;
        }
        catch (InvalidOperationException) { return false; }
    }

    public void HideFor(int milliseconds)
    {
        _hiddenUntil = DateTime.UtcNow.AddMilliseconds(milliseconds);
        if (IsVisible) Hide();
        _unhide.Start();
    }

    public void SetLevel(double level)
    {
        if (_view.Mode != "listening") return;
        _level = _level * 0.55 + level * 0.45;
        _haloScale.ScaleX = _haloScale.ScaleY = 1 + _level * 0.65;
        _halo.Opacity = 0.35 + _level * 0.55;
    }

    public void Apply(OverlayView view)
    {
        _view = view;
        bool wanted = view.Mode == "prompt"
                      || view.Visibility == "always"
                      || (view.Visibility == "active" && view.Mode != "idle");
        if (DateTime.UtcNow < _hiddenUntil) wanted = false;

        var (from, to, glyph) = view.Mode switch
        {
            "listening" => ("#2FD6EE", "#3DDC97", ""),
            "transcribing" => ("#2FD6EE", "#7C6CFF", ""),
            "thinking" => ("#8B7BFF", "#C08BFF", ""),
            "acting" => ("#7C6CFF", "#2FD6EE", ""),
            "speaking" => ("#3DDC97", "#2FD6EE", ""),
            "paused" => ("#F5B53D", "#FF9F5A", ""),
            "prompt" => view.Prompt?.Kind == "risk" ? ("#FF9F5A", "#FF6B6B", "") : ("#F5B53D", "#FFD479", ""),
            "notice" => ("#7F83AD", "#565A86", ""),
            _ => view.ControlActive ? ("#7C6CFF", "#2FD6EE", "") : ("#565A86", "#444873", ""),
        };
        _orb.Fill = Gradient(from, to);
        _orbGlyph.Text = glyph;
        _title.Text = string.IsNullOrWhiteSpace(view.Title) ? "Pocket Agent" : view.Title;
        _caption.Text = view.Caption ?? "";
        _caption.Visibility = string.IsNullOrWhiteSpace(view.Caption) ? Visibility.Collapsed : Visibility.Visible;
        if (view.Mode != "listening")
        {
            _halo.Opacity = 0;
            _haloScale.ScaleX = _haloScale.ScaleY = 1;
            _level = 0;
        }
        Animate(view.Mode);
        BuildButtons(view);
        BuildChoices(view.Prompt);
        _shell.CornerRadius = new CornerRadius(view.Prompt is null && _caption.Visibility != Visibility.Visible ? 26 : 20);

        if (wanted && !IsVisible)
        {
            Opacity = 0;
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            Reposition();
        }
        else if (!wanted && IsVisible)
        {
            Hide();
        }
        if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reposition));
    }

    private void Animate(string mode)
    {
        bool pulse = mode is "thinking" or "acting" or "transcribing";
        if (pulse == (_animatedMode is "thinking" or "acting" or "transcribing") && _animatedMode.Length > 0) { _animatedMode = mode; return; }
        _animatedMode = mode;
        if (pulse)
        {
            _orb.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        else
        {
            _orb.BeginAnimation(OpacityProperty, null);
            _orb.Opacity = 1;
        }
    }

    private void BuildButtons(OverlayView v)
    {
        _buttons.Children.Clear();
        var list = new List<(string Glyph, string Command, string Tip)>();
        switch (v.Mode)
        {
            case "listening":
                list.Add(("", "talk", "Done talking"));
                break;
            case "thinking":
            case "acting":
            case "transcribing":
                if (v.TaskRunning)
                {
                    list.Add(("", "pause", "Pause"));
                    list.Add(("", "stop", "Stop the task"));
                }
                break;
            case "paused":
                list.Add(("", "resume", "Resume"));
                list.Add(("", "stop", "Stop the task"));
                break;
            case "speaking":
                list.Add(("", "hush", "Stop speaking"));
                if (v.TaskRunning) list.Add(("", "stop", "Stop the task"));
                break;
            case "prompt":
                break;
            default:
                list.Add(("", "talk", v.TalkHint));
                list.Add(("", "open", "Open Pocket Agent"));
                break;
        }
        foreach (var (glyph, command, tip) in list) _buttons.Children.Add(IconButton(glyph, command, tip, command == "stop"));
    }

    private Border IconButton(string glyph, string command, string tip, bool danger)
    {
        // Coral for stop (--coral #ff6b6b), translucent light for everything else.
        var normal = new SolidColorBrush(danger ? Color.FromArgb(0x33, 0xFF, 0x6B, 0x6B) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        var hover = new SolidColorBrush(danger ? Color.FromArgb(0x55, 0xFF, 0x6B, 0x6B) : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        var b = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(3, 0, 0, 0),
            Background = normal, Cursor = Cursors.Hand, ToolTip = tip,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = Icons, FontSize = 12,
                Foreground = danger ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9C, 0x9C)) : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = normal;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; Raise(command); };
        return b;
    }

    private void BuildChoices(PromptView? prompt)
    {
        _choices.Children.Clear();
        if (prompt is null)
        {
            _choices.Visibility = Visibility.Collapsed;
            return;
        }
        _choices.Visibility = Visibility.Visible;
        foreach (var choice in prompt.Choices)
        {
            var (bg, fg, hover) = choice.Style switch
            {
                "primary" => (Color.FromRgb(0x7C, 0x6C, 0xFF), Colors.White, Color.FromRgb(0x8E, 0x80, 0xFF)),
                "danger" => (Color.FromArgb(0x33, 0xFF, 0x6B, 0x6B), Color.FromRgb(0xFF, 0x9C, 0x9C), Color.FromArgb(0x55, 0xFF, 0x6B, 0x6B)),
                _ => (Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), Colors.White, Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF)),
            };
            var normalBrush = new SolidColorBrush(bg);
            var hoverBrush = new SolidColorBrush(hover);
            var button = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 6, 6),
                Background = normalBrush,
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = choice.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(fg) },
            };
            var id = $"answer:{prompt.Id}:{choice.Id}";
            button.MouseEnter += (_, _) => button.Background = hoverBrush;
            button.MouseLeave += (_, _) => button.Background = normalBrush;
            button.MouseLeftButtonDown += (_, e) => e.Handled = true;
            button.MouseLeftButtonUp += (_, e) => { e.Handled = true; Raise(id); };
            _choices.Children.Add(button);
        }
    }
}
