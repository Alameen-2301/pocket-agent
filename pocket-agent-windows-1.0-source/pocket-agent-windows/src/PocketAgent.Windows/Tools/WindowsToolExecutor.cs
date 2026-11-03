using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Safety;
using PocketAgent.Core.Tools;
using PocketAgent.Windows.Overlay;
using WinForms = System.Windows.Forms;

namespace PocketAgent.Windows.Tools;

/// <summary>
/// The windows_* tools. Ported from the original WindowsTools (same names, arguments and safety
/// rules: per-app approval, password fields refused, secure desktops and Pocket Agent's own UI
/// refused, no arbitrary programs) and extended with more input, window switching, websites,
/// marked screenshots and OCR.
/// </summary>
internal sealed class WindowsToolExecutor : IToolExecutor
{
    private readonly IToolServices _s;
    private readonly OverlayService _overlay;
    private Snapshot? _snapshot;
    private Dictionary<int, IntPtr>? _windows;

    private sealed record ElementRef(AutomationElement Element, string Type, string Name, Rectangle Bounds, bool Interactive);
    private sealed record Snapshot(IntPtr Window, Dictionary<int, ElementRef> Elements);
    private sealed record Target(IntPtr Hwnd, int ProcessId, string Path, string Title)
    {
        public string Exe => System.IO.Path.GetFileName(Path);
    }
    private enum ClickKind { Left, Double, Right, Long }

    private static readonly string[] SecureDesktopExes = ["consent.exe", "logonui.exe", "winlogon.exe", "credentialuibroker.exe", "msiexec.exe", "lockapp.exe"];

    private static readonly HashSet<string> InteractiveTypes = new(StringComparer.Ordinal)
    {
        "Button", "CheckBox", "ComboBox", "Edit", "Hyperlink", "ListItem", "MenuItem", "RadioButton", "SplitButton",
        "TabItem", "TreeItem", "DataItem", "Slider", "Spinner", "Document", "MenuBar",
    };

    private static readonly HashSet<string> ActionTypes = new(StringComparer.Ordinal) { "Button", "SplitButton", "Hyperlink", "MenuItem", "ListItem" };

    private static readonly string[] MessagingExes = ["whatsapp.exe", "telegram.exe", "signal.exe", "slack.exe", "discord.exe", "teams.exe", "ms-teams.exe", "outlook.exe", "olk.exe", "thunderbird.exe", "skype.exe"];
    private static readonly string[] MessagingTitles = ["whatsapp", "messenger", "telegram", "slack", "discord", "microsoft teams", "gmail", "outlook", "linkedin", "instagram", "x.com", "signal"];

    public WindowsToolExecutor(IToolServices services, OverlayService overlay)
    {
        _s = services;
        _overlay = overlay;
    }

    public ToolOutcome Execute(string name, JsonElement args)
    {
        try
        {
            if (!_s.ControlActive) throw new InvalidOperationException("Windows control is switched off.");
            switch (name)
            {
                case "windows_status": return Status();
                case "windows_wait": return Wait(args);
                case "windows_open_app": return OpenApp(args);
                case "windows_open_url": return OpenUrl(args);
                case "windows_list_windows": return ListWindows();
                case "windows_focus_window": return FocusWindow(args);
                case "windows_press": return Press(args);
            }
            var target = RequireTarget();
            return name switch
            {
                "windows_get_screen" => ReadScreen(target),
                "windows_screenshot" => Screenshot(target, args),
                "windows_read_text" => ReadText(target, args),
                "windows_tap" => Click(target, args, ClickKind.Left),
                "windows_double_click" => Click(target, args, ClickKind.Double),
                "windows_right_click" => Click(target, args, ClickKind.Right),
                "windows_long_press" => Click(target, args, ClickKind.Long),
                "windows_hover" => Hover(target, args),
                "windows_type" => Type(target, args),
                "windows_key" => Key(target, args),
                "windows_scroll" => Scroll(target, args),
                "windows_swipe" => Drag(target, args),
                _ => ToolOutcome.Fail("Unknown tool: " + name),
            };
        }
        catch (ToolDeniedException e) { return ToolOutcome.Fail(e.Message, denied: true); }
        catch (ToolInterruptedException e) { return ToolOutcome.Fail(e.Message, interrupted: true); }
        catch (OperationCanceledException) { return ToolOutcome.Fail("Interrupted: the user took over or stopped the task. Look at the screen again before continuing.", interrupted: true); }
        catch (ElementNotAvailableException) { return ToolOutcome.Fail("That element disappeared. Read the screen again."); }
        catch (Exception e) { return ToolOutcome.Fail(e.Message); }
    }

    // ---------------------------------------------------------------- argument helpers (as in the original)

    private static string? Str(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)Math.Round(d);
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var i)) return i;
        return null;
    }
    private static double? Num(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }
    private static bool Bool(JsonElement obj, string key, bool defaultValue)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : defaultValue;
    private static int Coordinate(JsonElement obj, string key) => (int)Math.Round(Num(obj, key) ?? throw new InvalidOperationException(key + " required."));

    private void CheckInterrupt()
    {
        if (!_s.ControlActive) throw new ToolInterruptedException("Windows control was switched off.");
        if (_s.InterruptRequested) throw new ToolInterruptedException("Interrupted: the user took over. Look at the screen again before continuing.");
    }

    private void Pause(int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            CheckInterrupt();
            Thread.Sleep(Math.Min(50, Math.Max(1, milliseconds - (int)sw.ElapsedMilliseconds)));
        }
    }

    // ---------------------------------------------------------------- foreground target and approval

    private ToolOutcome Status()
    {
        var r = WinForms.SystemInformation.VirtualScreen;
        string foreground = "unavailable";
        try
        {
            var t = Foreground();
            foreground = $"{t.Exe} — {t.Title}";
        }
        catch (ToolDeniedException) { foreground = "an app on the never-allow list"; }
        catch { }
        var monitors = string.Join("; ", WinForms.Screen.AllScreens.Select((s, i) =>
            $"{i + 1}{(s.Primary ? " (primary)" : "")}: left={s.Bounds.Left} top={s.Bounds.Top} {s.Bounds.Width}x{s.Bounds.Height} at {Math.Round(Native.DpiScaleAt(s.Bounds.Left + 10, s.Bounds.Top + 10) * 100)}%"));
        return ToolOutcome.Ok($"Windows desktop: {(Native.InteractiveDesktop() ? "unlocked" : "locked or protected")}. " +
                              $"Foreground: {foreground}. Virtual screen: left={r.Left} top={r.Top} width={r.Width} height={r.Height}. " +
                              $"Monitors: {monitors}.", $"Foreground: {foreground}");
    }

    private Target Foreground()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("No foreground window.");
        int pid = Native.ProcessIdOf(hwnd);
        if (pid == _s.OwnProcessId) throw new InvalidOperationException("Pocket Agent cannot control itself. Switch to a different window (windows_list_windows and windows_focus_window).");
        string path = Native.ProcessPath(pid);
        if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Could not identify the foreground app (it may be running as administrator).");
        var exe = Path.GetFileName(path);
        if (SecureDesktopExes.Contains(exe, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("System security or installation screens cannot be controlled.");
        if (_s.IsBlocked(path)) throw new ToolDeniedException($"{exe} is on the never-allow list, so Pocket Agent won't read or control it.");
        return new Target(hwnd, pid, path, Native.WindowTitle(hwnd));
    }

    private Target RequireTarget()
    {
        if (!Native.InteractiveDesktop()) throw new InvalidOperationException("Windows is locked, inactive, or showing a secure desktop. Unlock it yourself.");
        var target = Foreground();
        _s.RequireApproval(ApprovalStore.AppKey(target.Path), target.Exe, target.Path + "\n" + target.Title);
        RestoreTarget(target);
        if (!Native.InteractiveDesktop() || Native.GetForegroundWindow() != target.Hwnd)
            throw new InvalidOperationException("Foreground changed during approval. Focus the intended app and retry.");
        CheckInterrupt();
        return target;
    }

    /// <summary>
    /// If the user answered a prompt in Pocket Agent's window, Pocket Agent is now in front:
    /// give the foreground back to the app being controlled (as the original did).
    /// </summary>
    private void RestoreTarget(Target target)
    {
        IntPtr front = Native.GetForegroundWindow();
        if (Native.ProcessIdOf(front) == _s.OwnProcessId && Native.IsWindow(target.Hwnd))
        {
            Native.ForceForeground(target.Hwnd);
            Thread.Sleep(150);
        }
    }

    private void EnsureForeground(Target target)
    {
        CheckInterrupt();
        if (!Native.InteractiveDesktop() || Native.GetForegroundWindow() != target.Hwnd)
            throw new InvalidOperationException("Control stopped, Windows locked, or the foreground window changed.");
    }

    // ---------------------------------------------------------------- reading the screen

    private Dictionary<int, ElementRef> Scan(Target target, List<string>? lines)
    {
        var map = new Dictionary<int, ElementRef>();
        var root = AutomationElement.FromHandle(target.Hwnd)
                   ?? throw new InvalidOperationException("Windows UI Automation could not read this app. Use windows_screenshot or windows_read_text.");
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        int n = 0, visits = 0, chars = 0;
        while (queue.Count > 0 && visits < 500 && chars < 18000)
        {
            var (el, depth) = queue.Dequeue();
            visits++;
            try
            {
                var cur = el.Current;
                var rect = cur.BoundingRectangle;
                bool password = cur.IsPassword;
                var type = cur.ControlType.ProgrammaticName.Replace("ControlType.", "");
                var label = password ? "[password field — redacted]" : (cur.Name ?? "");
                if (label.Length > 180) label = label[..180] + "…";
                if (!cur.IsOffscreen && rect.Width > 1 && rect.Height > 1)
                {
                    var bounds = new Rectangle((int)Math.Round(rect.Left), (int)Math.Round(rect.Top), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
                    map[++n] = new ElementRef(el, type, password ? "" : label, bounds, InteractiveTypes.Contains(type));
                    if (lines is not null)
                    {
                        var extra = new StringBuilder();
                        if (!password && (type is "Edit" or "ComboBox") && el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                        {
                            var value = ((ValuePattern)vp).Current.Value ?? "";
                            if (value.Length > 0 && value != label) extra.Append($" value=\"{(value.Length > 100 ? value[..100] + "…" : value)}\"");
                        }
                        if (!cur.IsEnabled) extra.Append(" (disabled)");
                        var line = $"{n}. {type} {label}{extra} [{bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}]";
                        lines.Add(line);
                        chars += line.Length;
                    }
                }
                if (depth >= 9 || queue.Count > 300) continue;
                var child = walker.GetFirstChild(el);
                int siblings = 0;
                while (child is not null && siblings++ < 120)
                {
                    queue.Enqueue((child, depth + 1));
                    child = walker.GetNextSibling(child);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        _snapshot = new Snapshot(target.Hwnd, map);
        return map;
    }

    private ToolOutcome ReadScreen(Target target)
    {
        var lines = new List<string> { $"Foreground: {target.Title} ({target.Exe})" };
        var map = Scan(target, lines);
        _s.Log("action", $"Read {map.Count} elements in {target.Exe}");
        return ToolOutcome.Ok(string.Join("\n", lines) + "\nElement numbers expire after an action. Password values are redacted.",
            $"Read {map.Count} elements in {target.Exe}");
    }

    private ElementRef Element(Target target, int id)
    {
        if (_snapshot is null || _snapshot.Window != target.Hwnd || !_snapshot.Elements.TryGetValue(id, out var el))
            throw new InvalidOperationException("Element number is missing or expired. Call windows_get_screen or windows_screenshot again.");
        return el;
    }

    private Rectangle CaptureArea(Target target, string scope)
    {
        var desktop = WinForms.SystemInformation.VirtualScreen;
        if (scope == "desktop") return desktop;
        var r = Native.WindowBounds(target.Hwnd);
        var area = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), desktop);
        if (area.Width < 2 || area.Height < 2) throw new InvalidOperationException("The window is off-screen or minimised.");
        return area;
    }

    private Bitmap Grab(Rectangle area) => _overlay.CaptureWithoutOverlays(() => ScreenCapture.Capture(area));

    private ToolOutcome Screenshot(Target target, JsonElement args)
    {
        var scope = Str(args, "scope") == "desktop" ? "desktop" : "window";
        bool marks = Bool(args, "marks", scope == "window");
        var area = CaptureArea(target, scope);
        Dictionary<int, ElementRef>? elements = null;
        if (marks)
        {
            try { elements = Scan(target, null); }
            catch (InvalidOperationException) { elements = null; }
        }
        using var original = Grab(area);
        using var reduced = ScreenCapture.Scale(original, scope == "desktop" ? 2000 : 1600, out double scale);
        var listed = new List<string>();
        if (elements is not null)
        {
            var visible = elements.Where(e => e.Value.Interactive && area.IntersectsWith(e.Value.Bounds)).ToList();
            ScreenCapture.DrawMarks(reduced, visible.Select(e => new Mark(e.Key, e.Value.Bounds)), area.Location, scale);
            int chars = 0;
            foreach (var (number, el) in visible)
            {
                var entry = $"{number} {el.Type} “{(el.Name.Length > 60 ? el.Name[..60] + "…" : el.Name)}”";
                if (chars + entry.Length > 5000) { listed.Add("…"); break; }
                listed.Add(entry);
                chars += entry.Length;
            }
        }
        var jpeg = ScreenCapture.EncodeJpeg(reduced, 70);
        int w = reduced.Width, h = reduced.Height;
        var text = new StringBuilder();
        text.Append($"Screenshot {w}x{h} of the {(scope == "desktop" ? "virtual desktop" : $"{target.Exe} window")}: left={area.Left}, top={area.Top}, ")
            .Append($"width={area.Width}, height={area.Height}. Convert screenshot pixel (px,py) into tool coordinates: ")
            .Append($"x=left+px*{area.Width}/{w}, y=top+py*{area.Height}/{h}.");
        if (listed.Count > 0)
            text.Append("\nNumbered boxes are clickable elements (same numbers as windows_get_screen; they expire after an action): ")
                .Append(string.Join("; ", listed));
        var outcome = new ToolOutcome { Summary = $"Screenshot of {(scope == "desktop" ? "the desktop" : target.Exe)}", Thumbnail = ScreenCapture.Thumbnail(reduced) };
        outcome.Content.Add(new TextPart(text.ToString()));
        outcome.Content.Add(new ImagePart("image/jpeg", Convert.ToBase64String(jpeg)));
        _s.Log("action", "Captured a screenshot of " + (scope == "desktop" ? "the desktop" : target.Exe));
        return outcome;
    }

    private ToolOutcome ReadText(Target target, JsonElement args)
    {
        var scope = Str(args, "scope") == "desktop" ? "desktop" : "window";
        var area = CaptureArea(target, scope);
        using var bitmap = Grab(area);
        var lines = OcrReader.Read(bitmap);
        var sb = new StringBuilder($"Text found by OCR in {(scope == "desktop" ? "the desktop" : target.Exe)} (screen coordinates [left,top,width,height]):\n");
        int count = 0;
        foreach (var line in lines)
        {
            var b = line.Bounds;
            var entry = $"{++count}. \"{line.Text}\" [{area.Left + b.Left},{area.Top + b.Top},{b.Width},{b.Height}]\n";
            if (sb.Length + entry.Length > 14000) { sb.Append("…\n"); break; }
            sb.Append(entry);
        }
        if (count == 0) sb.Append("(no text found)\n");
        sb.Append("Click a line at the centre of its box with windows_tap x/y.");
        _s.Log("action", $"Read {count} lines of text with OCR in {target.Exe}");
        return ToolOutcome.Ok(sb.ToString(), $"OCR found {count} lines in {target.Exe}");
    }

    // ---------------------------------------------------------------- pointer

    private (int X, int Y, string? Label, string? Type) Position(Target target, JsonElement args)
    {
        if (Int(args, "element") is int id)
        {
            var el = Element(target, id);
            var r = el.Element.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) throw new InvalidOperationException("Element has no clickable bounds.");
            var (label, type) = ActionableLabel(el.Element) ?? (el.Name, el.Type);
            return ((int)Math.Round(r.Left + r.Width / 2), (int)Math.Round(r.Top + r.Height / 2), label, type);
        }
        int x = Coordinate(args, "x"), y = Coordinate(args, "y");
        string? pointLabel = null, pointType = null;
        try
        {
            var at = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (at is not null && ActionableLabel(at) is { } found)
            {
                pointLabel = found.Label;
                pointType = found.Type;
            }
        }
        catch { }
        return (x, y, pointLabel, pointType);
    }

    /// <summary>The name of the button, link or menu item at or above this element (for the risk check).</summary>
    private static (string Label, string Type)? ActionableLabel(AutomationElement start)
    {
        var el = start;
        for (int depth = 0; el is not null && depth < 4; depth++)
        {
            try
            {
                var cur = el.Current;
                var type = cur.ControlType.ProgrammaticName.Replace("ControlType.", "");
                if (ActionTypes.Contains(type) && !string.IsNullOrWhiteSpace(cur.Name)) return (cur.Name, type);
            }
            catch (ElementNotAvailableException) { return null; }
            el = TreeWalker.ControlViewWalker.GetParent(el);
        }
        return null;
    }

    private void CheckRiskyClick(Target target, string? label)
    {
        if (!_s.ConfirmRiskyEnabled || string.IsNullOrWhiteSpace(label)) return;
        var phrase = _s.Risk.MatchLabel(label);
        if (phrase is null) return;
        var shown = label.Length > 50 ? label[..50] + "…" : label;
        _s.ConfirmRisk($"click “{shown}”", target.Exe, $"“{shown}” looks like it sends, deletes, pays or submits something.");
        RestoreTarget(target);
    }

    private ToolOutcome Click(Target target, JsonElement args, ClickKind kind)
    {
        var (x, y, label, _) = Position(target, args);
        CheckRiskyClick(target, label);
        _s.ClearPoint(x, y);
        _s.PreviewClick(x, y);
        EnsureForeground(target);
        Native.MoveTo(x, y);
        switch (kind)
        {
            case ClickKind.Left:
                Native.MouseButton(Native.MouseLeftDown);
                Native.MouseButton(Native.MouseLeftUp);
                break;
            case ClickKind.Double:
                Native.MouseButton(Native.MouseLeftDown);
                Native.MouseButton(Native.MouseLeftUp);
                Thread.Sleep(70);
                Native.MouseButton(Native.MouseLeftDown);
                Native.MouseButton(Native.MouseLeftUp);
                break;
            case ClickKind.Right:
                Native.MouseButton(Native.MouseRightDown);
                Native.MouseButton(Native.MouseRightUp);
                break;
            case ClickKind.Long:
                Native.MouseButton(Native.MouseLeftDown);
                try { Pause(800); }
                finally { Native.MouseButton(Native.MouseLeftUp); }
                break;
        }
        _snapshot = null;
        var verb = kind switch { ClickKind.Double => "Double-clicked", ClickKind.Right => "Right-clicked", ClickKind.Long => "Long-clicked", _ => "Clicked" };
        var on = string.IsNullOrWhiteSpace(label) ? "" : $" on “{(label.Length > 60 ? label[..60] + "…" : label)}”";
        _s.Log("action", $"{verb} ({x},{y}){on} in {target.Exe}");
        return ToolOutcome.Ok($"{verb} ({x},{y}){on}. Read the screen again before using element numbers.", $"{verb}{on} in {target.Exe}");
    }

    private ToolOutcome Hover(Target target, JsonElement args)
    {
        var (x, y, label, _) = Position(target, args);
        EnsureForeground(target);
        Native.MoveTo(x, y);
        Pause(500);
        _snapshot = null;
        return ToolOutcome.Ok($"The pointer is over ({x},{y}){(label is null ? "" : $" “{label}”")}. Tooltips or hover menus may have appeared; read the screen.",
            $"Hovered in {target.Exe}");
    }

    private ToolOutcome Scroll(Target target, JsonElement args)
    {
        var dir = Str(args, "direction") ?? "";
        int notches = Math.Clamp(Int(args, "amount") ?? 5, 1, 15);
        EnsureForeground(target);
        if (Int(args, "element") is not null || Num(args, "x") is not null)
        {
            var (x, y, _, _) = Position(target, args);
            Native.MoveTo(x, y);
        }
        else
        {
            var r = Native.WindowBounds(target.Hwnd);
            Native.GetCursorPos(out var p);
            if (p.X < r.Left || p.X > r.Right || p.Y < r.Top || p.Y > r.Bottom) Native.MoveTo(r.Left + r.Width / 2, r.Top + r.Height / 2);
        }
        int delta = 120 * notches;
        switch (dir)
        {
            case "up": Native.Wheel(false, delta); break;
            case "down": Native.Wheel(false, -delta); break;
            case "left": Native.Wheel(true, -delta); break;
            case "right": Native.Wheel(true, delta); break;
            default: throw new InvalidOperationException("direction must be up, down, left, or right.");
        }
        _snapshot = null;
        _s.Log("action", $"Scrolled {dir} in {target.Exe}");
        return ToolOutcome.Ok($"Scrolled {dir} {notches} notches. Read the screen again.", $"Scrolled {dir} in {target.Exe}");
    }

    private ToolOutcome Drag(Target target, JsonElement args)
    {
        int x1 = Coordinate(args, "x1"), y1 = Coordinate(args, "y1"), x2 = Coordinate(args, "x2"), y2 = Coordinate(args, "y2");
        int ms = Math.Clamp(Int(args, "duration_ms") ?? 300, 50, 3000);
        _s.ClearPoint(x1, y1);
        EnsureForeground(target);
        Native.MoveTo(x1, y1);
        Native.MouseButton(Native.MouseLeftDown);
        try
        {
            for (int step = 1; step <= 15; step++)
            {
                CheckInterrupt();
                if (!Native.InteractiveDesktop()) throw new InvalidOperationException("Windows became locked.");
                Native.MoveTo(x1 + (x2 - x1) * step / 15, y1 + (y2 - y1) * step / 15);
                Thread.Sleep(ms / 15);
            }
        }
        finally { Native.MouseButton(Native.MouseLeftUp); }
        _snapshot = null;
        _s.Log("action", "Dragged the mouse in " + target.Exe);
        return ToolOutcome.Ok("Dragged. Read the screen again.", "Dragged in " + target.Exe);
    }

    // ---------------------------------------------------------------- keyboard

    private bool IsMessagingContext(Target target)
    {
        if (MessagingExes.Contains(target.Exe, StringComparer.OrdinalIgnoreCase)) return true;
        var title = target.Title.ToLowerInvariant();
        return MessagingTitles.Any(t => title.Contains(t));
    }

    private ToolOutcome Type(Target target, JsonElement args)
    {
        string content = Str(args, "text") ?? throw new InvalidOperationException("text is required.");
        if (content.Length > 4000) throw new InvalidOperationException("Text limit is 4000 characters.");
        AutomationElement el;
        if (Int(args, "element") is int id) el = Element(target, id).Element;
        else el = AutomationElement.FocusedElement ?? throw new InvalidOperationException("Focus a text field first, or give its element number.");
        var cur = el.Current;
        if (cur.IsPassword) throw new InvalidOperationException("Password fields are not allowed. Type the password yourself.");
        if (!BelongsTo(el, target)) throw new InvalidOperationException("The focused field belongs to a different app. Click the field you want first.");
        var type = cur.ControlType;
        bool editable = type == ControlType.Edit || type == ControlType.Document || type == ControlType.ComboBox || WritableValue(el);
        if (!editable)
            throw new InvalidOperationException("Target is not a verified editable field. Focus a text field or supply its element number.");
        if (Int(args, "element") is not null)
        {
            try { el.SetFocus(); } catch (InvalidOperationException) { }
        }
        bool submit = Bool(args, "submit", false);
        if (submit && _s.ConfirmRiskyEnabled && IsMessagingContext(target))
        {
            _s.ConfirmRisk("type a message and press Enter to send it", target.Exe, "Pressing Enter here probably sends the message.");
            RestoreTarget(target);
        }
        EnsureForeground(target);
        bool replace = Bool(args, "replace", type == ControlType.Edit || type == ControlType.ComboBox);
        if (replace) Native.KeyCombo(KeyChord.VK_CONTROL, 0x41); // Ctrl+A
        Native.UnicodeType(content, () => _s.ControlActive && !_s.InterruptRequested && Native.InteractiveDesktop());
        if (submit) Native.KeyTap(0x0D);
        _snapshot = null;
        _s.Log("action", $"Typed {content.Length} characters in {target.Exe}");
        return ToolOutcome.Ok($"Typed {content.Length} characters; text not logged.", $"Typed {content.Length} characters in {target.Exe}");
    }

    /// <summary>
    /// True when the element is inside the target window. Store apps (Settings, Calculator…) draw their
    /// content from another process inside an ApplicationFrameHost window, so the process id alone is not enough.
    /// </summary>
    private static bool BelongsTo(AutomationElement el, Target target)
    {
        try
        {
            if (el.Current.ProcessId == target.ProcessId) return true;
            var walker = TreeWalker.RawViewWalker;
            var cur = el;
            for (int depth = 0; cur is not null && depth < 40; depth++)
            {
                int handle = cur.Current.NativeWindowHandle;
                if (handle != 0)
                {
                    var hwnd = new IntPtr(handle);
                    return hwnd == target.Hwnd || Native.GetAncestor(hwnd, Native.GA_ROOT) == target.Hwnd;
                }
                cur = walker.GetParent(cur);
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        return false;
    }

    private static bool WritableValue(AutomationElement el)
    {
        try
        {
            return el.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && !((ValuePattern)p).Current.IsReadOnly && el.Current.IsKeyboardFocusable;
        }
        catch { return false; }
    }

    private ToolOutcome Key(Target target, JsonElement args)
    {
        if (!KeyChord.TryParse(Str(args, "keys"), out var chord, out var error)) throw new InvalidOperationException(error);
        if (chord.IsBlocked(out var reason)) throw new InvalidOperationException(reason);
        int repeat = Math.Clamp(Int(args, "repeat") ?? 1, 1, 20);
        var pretty = KeyChord.Pretty(chord.Normalized);
        if (_s.ConfirmRiskyEnabled)
        {
            var risk = RiskPolicy.MatchChord(chord, target.Exe)
                       ?? (chord.Key == 0x0D && chord.Modifiers.Count == 0 && IsMessagingContext(target) ? "Enter probably sends the message in this app" : null);
            if (risk is not null)
            {
                _s.ConfirmRisk($"press {pretty}", target.Exe, risk);
                RestoreTarget(target);
            }
        }
        EnsureForeground(target);
        for (int i = 0; i < repeat; i++)
        {
            CheckInterrupt();
            if (chord.Key == KeyChord.VK_LWIN && chord.Modifiers.Count == 0) Native.KeyTap(KeyChord.VK_LWIN);
            else Native.Chord(chord.Modifiers, chord.Key);
            if (repeat > 1) Thread.Sleep(40);
        }
        _snapshot = null;
        _s.Log("action", $"Pressed {pretty}{(repeat > 1 ? $" ×{repeat}" : "")} in {target.Exe}");
        return ToolOutcome.Ok($"Pressed {pretty}{(repeat > 1 ? $" {repeat} times" : "")}. Read the screen again.", $"Pressed {pretty} in {target.Exe}");
    }

    private ToolOutcome Press(JsonElement args)
    {
        if (!Native.InteractiveDesktop()) throw new InvalidOperationException("Windows is locked or protected.");
        // System actions still require consent for the current foreground app.
        var target = RequireTarget();
        var action = Str(args, "button") ?? "";
        switch (action)
        {
            case "back": Native.KeyCombo(0x12, 0x25); break;           // Alt+Left
            case "home": Native.KeyCombo(0x5B, 0x44); break;           // Win+D
            case "recents": Native.KeyCombo(0x5B, 0x09); break;        // Win+Tab
            case "notifications": Native.KeyCombo(0x5B, 0x4E); break;  // Win+N
            case "quick_settings": Native.KeyCombo(0x5B, 0x41); break; // Win+A
            default: throw new InvalidOperationException("Unsupported button.");
        }
        _snapshot = null;
        _s.Log("action", "Pressed " + action + " in " + target.Exe);
        return ToolOutcome.Ok("Pressed " + action + ".", "Shortcut: " + action.Replace('_', ' '));
    }

    // ---------------------------------------------------------------- apps, websites and windows

    private ToolOutcome OpenApp(JsonElement args)
    {
        var name = Str(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new InvalidOperationException("Provide an app name, max 100 characters.");
        if (!Native.InteractiveDesktop()) throw new InvalidOperationException("Windows is locked or protected.");
        var app = AppCatalog.Find(name);
        if (app is null)
        {
            var suggestions = AppCatalog.Suggest(name);
            return ToolOutcome.Fail(suggestions.Count > 0
                ? $"No installed app named '{name}'. Close matches: {string.Join(", ", suggestions)}. Use the exact name."
                : $"No installed app named '{name}'. Use the exact Start menu name.");
        }
        if (_s.IsBlocked(app.Entry)) throw new ToolDeniedException($"{app.Name} is on the never-allow list.");
        // Never accept an arbitrary executable path or shell command from a tool argument.
        _s.RequireApproval(ApprovalStore.LaunchKey(app.Entry), app.Name, "Launch " + app.Name + "\n" + app.Entry);
        CheckInterrupt();
        if (!Native.InteractiveDesktop()) throw new InvalidOperationException("Windows became locked.");
        AppCatalog.Launch(app);
        _snapshot = null;
        _s.Log("action", "Launched " + app.Name);
        Thread.Sleep(600);
        return ToolOutcome.Ok($"Opened {app.Name}. Call windows_status and windows_get_screen next (it may take a moment to appear).", "Opened " + app.Name);
    }

    private ToolOutcome OpenUrl(JsonElement args)
    {
        var raw = Str(args, "url")?.Trim() ?? "";
        if (raw.Length is 0 or > 2048 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Give a full web address starting with http:// or https://.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("Web addresses with embedded user names or passwords are not allowed.");
        if (!Native.InteractiveDesktop()) throw new InvalidOperationException("Windows is locked or protected.");
        _s.RequireApproval(ApprovalStore.WebsitesKey, "your web browser", "Open " + uri.Host + " in the default browser");
        CheckInterrupt();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        _snapshot = null;
        _s.Log("action", "Opened " + uri.Host);
        Thread.Sleep(800);
        return ToolOutcome.Ok($"Opened {uri.Host} in the default browser. Call windows_status and windows_get_screen next.", "Opened " + uri.Host);
    }

    private List<(IntPtr Hwnd, string Title, string Exe, bool Minimized)> OpenWindows()
    {
        var list = new List<(IntPtr, string, string, bool)>();
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd) || Native.IsCloaked(hwnd)) return true;
                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                bool owned = Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero;
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
                if (owned && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
                var title = Native.WindowTitle(hwnd);
                if (string.IsNullOrWhiteSpace(title)) return true;
                int pid = Native.ProcessIdOf(hwnd);
                if (pid == _s.OwnProcessId) return true;
                var path = Native.ProcessPath(pid);
                var exe = string.IsNullOrEmpty(path) ? "unknown" : Path.GetFileName(path);
                if (exe.Equals("TextInputHost.exe", StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(path) && _s.IsBlocked(path)) exe += " (never-allow list)";
                list.Add((hwnd, title, exe, Native.IsIconic(hwnd)));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private ToolOutcome ListWindows()
    {
        var windows = OpenWindows();
        _windows = new Dictionary<int, IntPtr>();
        var sb = new StringBuilder("Open windows (front to back):\n");
        int n = 0;
        foreach (var w in windows.Take(60))
        {
            _windows[++n] = w.Hwnd;
            var title = w.Title.Length > 90 ? w.Title[..90] + "…" : w.Title;
            sb.Append($"{n}. {title} — {w.Exe}{(w.Minimized ? " [minimised]" : "")}\n");
        }
        if (n == 0) sb.Append("(none)\n");
        sb.Append("Use windows_focus_window with a window number to bring it to the front.");
        return ToolOutcome.Ok(sb.ToString(), $"{n} open windows");
    }

    private ToolOutcome FocusWindow(JsonElement args)
    {
        if (!Native.InteractiveDesktop()) throw new InvalidOperationException("Windows is locked or protected.");
        IntPtr hwnd = IntPtr.Zero;
        if (Int(args, "window") is int id)
        {
            if (_windows is null || !_windows.TryGetValue(id, out hwnd)) throw new InvalidOperationException("Window number is unknown or expired. Call windows_list_windows again.");
        }
        else if (Str(args, "title") is { Length: > 0 } fragment)
        {
            var matches = OpenWindows().Where(w => w.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) throw new InvalidOperationException($"No open window title contains '{fragment}'. Call windows_list_windows.");
            if (matches.Count > 1 && !matches.All(m => m.Hwnd == matches[0].Hwnd))
                throw new InvalidOperationException($"{matches.Count} windows match '{fragment}': " + string.Join("; ", matches.Take(5).Select(m => m.Title)) + ". Use a window number.");
            hwnd = matches[0].Hwnd;
        }
        else throw new InvalidOperationException("Give a window number from windows_list_windows, or a title.");
        if (!Native.IsWindow(hwnd)) throw new InvalidOperationException("That window has closed. Call windows_list_windows again.");
        int pid = Native.ProcessIdOf(hwnd);
        if (pid == _s.OwnProcessId) throw new InvalidOperationException("Pocket Agent cannot control its own window.");
        var path = Native.ProcessPath(pid);
        if (!string.IsNullOrEmpty(path) && _s.IsBlocked(path)) throw new ToolDeniedException($"{Path.GetFileName(path)} is on the never-allow list.");
        CheckInterrupt();
        bool ok = Native.ForceForeground(hwnd);
        _snapshot = null;
        var title = Native.WindowTitle(hwnd);
        if (!ok) return ToolOutcome.Fail($"Windows did not allow switching to “{title}”. Try windows_press recents, or ask the user to click it.");
        _s.Log("action", $"Switched to {Path.GetFileName(path)}");
        return ToolOutcome.Ok($"Switched to “{title}” ({Path.GetFileName(path)}). Read the screen next.", $"Switched to {Path.GetFileName(path)}");
    }

    private ToolOutcome Wait(JsonElement args)
    {
        double seconds = Num(args, "seconds") ?? 1;
        if (double.IsNaN(seconds)) throw new InvalidOperationException("Invalid duration.");
        seconds = Math.Clamp(seconds, 0.2, 10);
        Pause((int)(seconds * 1000));
        return ToolOutcome.Ok("Waited " + seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " seconds.");
    }
}
