using PocketAgent.Core.Safety;
using PocketAgent.Core.Tools;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.App;

public sealed partial class AppController
{
    private sealed class PendingPrompt
    {
        public required PromptView View { get; init; }
        public TaskCompletionSource<string> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly List<PendingPrompt> _prompts = new();
    private volatile bool _automationPaused;
    private string _pauseReason = "";

    private static readonly PromptChoice[] AppChoices =
    [
        new("session", "Allow this session", "primary"),
        new("always", "Always allow"),
        new("deny", "Deny"),
        new("block", "Never allow", "danger"),
    ];

    private static readonly PromptChoice[] LaunchChoices =
    [
        new("session", "Allow this session", "primary"),
        new("always", "Always allow"),
        new("deny", "Deny"),
    ];

    private static readonly PromptChoice[] RiskChoices =
    [
        new("allow", "Allow once", "primary"),
        new("deny", "Don't do it", "danger"),
    ];

    // ---------------------------------------------------------------- IToolServices

    public bool InterruptRequested => _automationPaused || !_controlActive;
    public RiskPolicy Risk => _risk;
    public bool ConfirmRiskyEnabled => _settings.Current.ConfirmRiskySteps;
    public bool IsBlocked(string exePath) => _approvals.IsBlocked(exePath);

    public void RequireApproval(string key, string appName, string detail)
    {
        if (!_controlActive) throw new ToolDeniedException("Windows control is switched off.");
        if (_approvals.IsAllowed(key)) return;
        bool isApp = key.StartsWith("exe:", StringComparison.OrdinalIgnoreCase);
        var title = key == ApprovalStore.WebsitesKey ? "Allow the assistant to open websites?"
            : isApp ? $"Allow the assistant to use {appName}?"
            : $"Allow the assistant to open {appName}?";
        var decision = Ask("app", title, detail, appName, isApp ? AppChoices : LaunchChoices);
        if (!_controlActive) throw new ToolDeniedException("Windows control has been switched off.");
        switch (decision)
        {
            case "session":
                _approvals.Allow(key, always: false);
                Log("approval", "Allowed this session: " + appName);
                return;
            case "always":
                _approvals.Allow(key, always: true);
                Log("approval", "Always allowed: " + appName);
                return;
            case "block":
                _approvals.Block(key);
                Log("approval", "Never allow: " + appName);
                throw new ToolDeniedException($"The user blocked {appName}. Do not try to use it.");
            case "timeout":
                Log("approval", "Approval timed out: " + appName);
                throw new ToolDeniedException("Approval timed out. Ask the user to approve in Pocket Agent and retry.");
            default:
                Log("approval", "Denied: " + appName);
                throw new ToolDeniedException($"The user denied access to {appName}.");
        }
    }

    public void ConfirmRisk(string action, string appName, string reason)
    {
        if (!ConfirmRiskyEnabled) return;
        if (!_controlActive) throw new ToolDeniedException("Windows control is switched off.");
        var decision = Ask("risk", $"Let the assistant {action}?", $"{reason}\nApp: {appName}", appName, RiskChoices);
        if (decision == "allow")
        {
            Log("approval", $"Confirmed: {action} in {appName}");
            return;
        }
        Log("approval", $"Declined: {action} in {appName}");
        throw new ToolDeniedException(decision == "timeout"
            ? $"Nobody confirmed “{action}”, so it was not done. Ask the user before trying again."
            : $"The user declined “{action}”. Do not retry; ask the user what they want instead.");
    }

    public void PreviewClick(int x, int y)
    {
        var s = _settings.Current;
        if (!s.ClickPreview || s.ClickPreviewMs <= 0) return;
        try { _p.Overlay.PreviewClick(x, y, s.ClickPreviewMs); } catch { }
    }

    public void ClearPoint(int x, int y)
    {
        try { if (_p.Overlay.Covers(x, y)) _p.Overlay.HideFor(1200); } catch { }
    }

    // ---------------------------------------------------------------- prompts

    /// <summary>Shows a prompt in the app and the bubble and blocks the calling (tool) thread for the answer.</summary>
    private string Ask(string kind, string title, string detail, string? appName, PromptChoice[] choices)
    {
        var timeout = TimeSpan.FromSeconds(_settings.Current.ApprovalTimeoutSeconds);
        var prompt = new PendingPrompt
        {
            View = new PromptView(Ids.New("prompt"), kind, title, detail, appName, choices,
                DateTimeOffset.Now.Add(timeout).ToUnixTimeMilliseconds()),
        };
        lock (_prompts) _prompts.Add(prompt);
        RequestPush();
        if (!_p.MainWindowVisible && _settings.Current.BubbleMode == "never")
            _p.Notify("Pocket Agent needs you", title);
        try
        {
            return prompt.Decision.Task.Wait(timeout + TimeSpan.FromSeconds(2)) ? prompt.Decision.Task.Result : "timeout";
        }
        finally
        {
            lock (_prompts) _prompts.Remove(prompt);
            RequestPush();
        }
    }

    public bool AnswerPrompt(string id, string choice)
    {
        PendingPrompt? p;
        lock (_prompts) p = _prompts.FirstOrDefault(x => x.View.Id == id);
        if (p is null) return false;
        if (!p.View.Choices.Any(c => c.Id == choice)) choice = "deny";
        return p.Decision.TrySetResult(choice);
    }

    private void ResolveAllPrompts(string choice)
    {
        List<PendingPrompt> all;
        lock (_prompts) all = _prompts.ToList();
        foreach (var p in all) p.Decision.TrySetResult(choice);
    }

    private void ExpirePrompts()
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        List<PendingPrompt> expired;
        lock (_prompts) expired = _prompts.Where(p => p.View.ExpiresAt <= now).ToList();
        foreach (var p in expired) p.Decision.TrySetResult("timeout");
    }

    private PromptView[] PromptViews()
    {
        lock (_prompts) return _prompts.Select(p => p.View).ToArray();
    }

    // ---------------------------------------------------------------- takeover, pause, stop

    /// <summary>Real (not injected) mouse or keyboard input from the user, reported by the input monitor.</summary>
    public void OnUserInput()
    {
        if (!_settings.Current.PauseOnUserInput || _automationPaused || _listening) return;
        bool agentActive = _agent.IsRunning && !_agent.IsPaused;
        bool externalActive = _controlActive && RecentToolActivity(TimeSpan.FromSeconds(10));
        if (!agentActive && !externalActive) return;
        lock (_prompts) if (_prompts.Count > 0) return; // the user is answering a prompt
        PauseAutomation("Paused — you took over. Press Resume or say “continue” when you're done.");
    }

    public void PauseAutomation(string reason)
    {
        _automationPaused = true;
        _pauseReason = reason;
        _agent.Pause(reason, takeover: true);
        Log("system", reason);
        RequestPush();
    }

    public void ResumeAutomation()
    {
        if (!_automationPaused && !_agent.IsPaused) return;
        _automationPaused = false;
        _pauseReason = "";
        _pausedForListening = false;
        _agent.Resume();
        Log("system", "Resumed.");
        RequestPush();
    }

    /// <summary>Stops the current task but leaves desktop control on.</summary>
    public void StopTask()
    {
        bool running = _agent.IsRunning;
        _automationPaused = false;
        _pausedForListening = false;
        ResolveAllPrompts("deny");
        _agent.Stop();
        CancelSpeech();
        if (running) Log("system", "Task stopped.");
        RequestPush();
    }

    /// <summary>Emergency stop (triple Esc): ends the task and switches desktop control off.</summary>
    public void Panic()
    {
        StopTask();
        _ = StopControlAsync("Emergency stop");
        ShowNotice("Emergency stop — desktop control is off.");
        _p.PlayCue("error");
    }
}
