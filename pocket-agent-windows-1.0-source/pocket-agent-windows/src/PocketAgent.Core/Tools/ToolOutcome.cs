using System.Text.Json;
using System.Text.Json.Nodes;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Safety;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.Tools;

/// <summary>What a tool produced: text and/or images, plus a one-line summary for the timeline.</summary>
public sealed class ToolOutcome
{
    public List<ContentPart> Content { get; } = new();
    public bool IsError { get; init; }
    /// <summary>Short human-readable result for the activity timeline ("Clicked 'Send' in outlook.exe").</summary>
    public string Summary { get; init; } = "";
    /// <summary>Optional small preview image (data URL) shown in the timeline only, never sent to the model.</summary>
    public string? Thumbnail { get; init; }
    /// <summary>Marks a result produced because the user paused or stopped automation.</summary>
    public bool Interrupted { get; init; }
    public bool Denied { get; init; }

    public static ToolOutcome Ok(string text, string? summary = null)
    {
        var o = new ToolOutcome { Summary = summary ?? FirstLine(text) };
        o.Content.Add(new TextPart(text));
        return o;
    }

    public static ToolOutcome Fail(string text, bool denied = false, bool interrupted = false)
    {
        var o = new ToolOutcome { IsError = true, Summary = FirstLine(text), Denied = denied, Interrupted = interrupted };
        o.Content.Add(new TextPart(text));
        return o;
    }

    public string Text => string.Join("\n", Content.OfType<TextPart>().Select(t => t.Text));

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 140 ? line[..140] + "…" : line;
    }

    /// <summary>MCP tools/call result shape: {"content":[...],"isError":bool}.</summary>
    public string ToMcpJson()
    {
        var content = new JsonArray();
        foreach (var part in Content)
        {
            switch (part)
            {
                case TextPart t:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                    break;
                case ImagePart i:
                    content.Add(new JsonObject { ["type"] = "image", ["mimeType"] = i.MediaType, ["data"] = i.Base64 });
                    break;
            }
        }
        if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = IsError ? "Tool failed." : "Done." });
        return new JsonObject { ["content"] = content, ["isError"] = IsError }.ToJsonString();
    }
}

/// <summary>The user declined, a prompt timed out, or the app is blocked.</summary>
public sealed class ToolDeniedException : Exception
{
    public ToolDeniedException(string message) : base(message) { }
}

/// <summary>The user took over (paused) or stopped automation while a tool was running.</summary>
public sealed class ToolInterruptedException : Exception
{
    public ToolInterruptedException(string message) : base(message) { }
}

/// <summary>Runs windows_* tools. Implemented by the Windows layer; called on background threads.</summary>
public interface IToolExecutor
{
    ToolOutcome Execute(string name, JsonElement args);
}

/// <summary>Services the tool implementation needs from the app: approvals, safety prompts and overlays.</summary>
public interface IToolServices
{
    bool ControlActive { get; }
    /// <summary>True while the user has taken over or a stop was requested; tools should abort promptly.</summary>
    bool InterruptRequested { get; }
    int OwnProcessId { get; }
    bool IsBlocked(string exePath);
    /// <summary>Blocks until the user decides. Throws <see cref="ToolDeniedException"/> on deny or timeout.</summary>
    void RequireApproval(string key, string appName, string detail);
    /// <summary>Asks the user to confirm a risky action. Throws <see cref="ToolDeniedException"/> when declined.</summary>
    void ConfirmRisk(string action, string appName, string reason);
    RiskPolicy Risk { get; }
    bool ConfirmRiskyEnabled { get; }
    /// <summary>Shows the click-preview ring and waits for it (no-op when disabled).</summary>
    void PreviewClick(int x, int y);
    /// <summary>Makes sure none of Pocket Agent's floating windows sit under this point.</summary>
    void ClearPoint(int x, int y);
    void Log(string category, string message);
}

/// <summary>Human-friendly titles for timeline steps. Never includes typed text.</summary>
public static class StepTitles
{
    public static string Describe(string tool, JsonObject args)
    {
        string Target()
        {
            if (args.Num("element") is double id) return $"element {Math.Round(id)}";
            if (args.Num("x") is double x && args.Num("y") is double y) return $"({Math.Round(x)}, {Math.Round(y)})";
            return "the screen";
        }
        string? Str(string key) => args.Str(key);

        return tool switch
        {
            "windows_status" => "Check what's on screen",
            "windows_get_screen" => "Read the screen",
            "windows_screenshot" => Str("scope") == "desktop" ? "Screenshot the desktop" : "Screenshot the window",
            "windows_tap" => "Click " + Target(),
            "windows_double_click" => "Double-click " + Target(),
            "windows_right_click" => "Right-click " + Target(),
            "windows_long_press" => "Long-press " + Target(),
            "windows_hover" => "Hover over " + Target(),
            "windows_type" => args.Str("text") is string text ? $"Type {text.Length} characters" : "Type text",
            "windows_key" => "Press " + KeyChord.Pretty(Str("keys") ?? "a key"),
            "windows_scroll" => "Scroll " + (Str("direction") ?? ""),
            "windows_swipe" => "Drag the mouse",
            "windows_press" => "Shortcut: " + (Str("button") ?? "").Replace('_', ' '),
            "windows_open_app" => "Open " + (Str("name") ?? "an app"),
            "windows_open_url" => "Open " + HostOf(Str("url")),
            "windows_list_windows" => "List open windows",
            "windows_focus_window" => "Switch window",
            "windows_read_text" => "Read text (OCR)",
            "windows_wait" => args.Num("seconds") is double sec ? $"Wait {sec:0.#} s" : "Wait",
            _ => tool,
        };
    }

    private static string HostOf(string? url)
    {
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u)) return u.Host;
        return "a website";
    }
}
