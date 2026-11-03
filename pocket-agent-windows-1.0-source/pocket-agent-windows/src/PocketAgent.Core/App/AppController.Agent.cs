using PocketAgent.Core.Agent;
using PocketAgent.Core.Providers;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.App;

public sealed partial class AppController
{
    private string _agentDetail = "";
    private string? _lastNarration;

    /// <summary>A request from the chat box or from speech. While a task runs it becomes extra instructions.</summary>
    public async Task SubmitAsync(string text, string source)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        AddChat("user", text, source);
        var notReady = AssistantNotReadyReason();
        if (notReady is not null)
        {
            AddChat("system", notReady);
            ShowNotice(notReady);
            if (source == "voice") Speak("I can't do that yet. " + notReady, "reply");
            RequestPush();
            return;
        }
        if (!_controlActive && _settings.Current.AutoStartControlOnTalk)
            await StartControlAsync("the assistant needs it").ConfigureAwait(false);
        if (_agent.IsRunning) Log("agent", "Added your message to the running task.");
        _agent.Submit(text);
        RequestPush();
    }

    public async Task NewConversationAsync()
    {
        StopTask();
        await _agent.ResetAsync().ConfigureAwait(false);
        lock (_chat) _chat.Clear();
        lock (_steps) { _steps.RemoveAll(s => s.Source == "assistant"); foreach (var k in _stepsById.Where(kv => kv.Value.Source == "assistant").Select(kv => kv.Key).ToList()) _stepsById.Remove(k); }
        Post("cleared", new { });
        SendHistory();
        RequestPush();
    }

    // ---------------------------------------------------------------- IAgentObserver

    public void OnStateChanged(AgentState state, string detail)
    {
        var previous = _lastAgentState;
        _lastAgentState = state;
        _agentDetail = detail;
        if (state == AgentState.Idle) _lastNarration = null;
        if ((previous == AgentState.Idle) != (state == AgentState.Idle)) ConfigureWakeWord(_settings.Current);
        RequestPush();
    }

    private AgentState _lastAgentState = AgentState.Idle;

    public void OnAssistantText(string runId, string text, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        AddChat("assistant", text, isFinal ? "final" : "narration", runId);
        if (isFinal)
        {
            Speak(text, "reply");
            if (!_p.MainWindowVisible) _p.Notify("Pocket Agent", TextUtil.Clip(text, 200));
        }
        else
        {
            _lastNarration = text;
            Speak(text, "narration");
        }
        RequestPush();
    }

    public void OnStep(AgentStep step) => UpsertStep(step);

    public void OnUsage(TokenUsage call, TokenUsage total) => RequestPush();

    public void OnRunFinished(string runId, RunEnd end, string? message)
    {
        _automationPaused = false;
        _pausedForListening = false;
        switch (end)
        {
            case RunEnd.Stopped:
                AddChat("system", "Stopped.", runId: runId);
                break;
            case RunEnd.Failed:
                AddChat("system", message ?? "The task failed.", runId: runId);
                Speak(message ?? "Sorry, something went wrong.", "reply");
                _p.PlayCue("error");
                break;
            case RunEnd.StepLimit:
                AddChat("system", message ?? "Step limit reached.", runId: runId);
                break;
            default:
                _p.PlayCue("done");
                break;
        }
        RequestPush();
    }

    private (double? Cost, string Model) CostEstimate()
    {
        var s = _settings.Current;
        var o = ProviderPresets.Resolve(s, _settings);
        s.Providers.TryGetValue(o.Id, out var config);
        var price = ProviderPresets.PriceFor(o.Model, config);
        return (ProviderPresets.EstimateCost(_agent.TotalUsage, price), o.Model);
    }
}
