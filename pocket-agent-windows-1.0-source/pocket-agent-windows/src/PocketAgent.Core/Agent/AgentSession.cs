using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PocketAgent.Core.Providers;
using PocketAgent.Core.Tools;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.Agent;

public enum AgentState { Idle, Thinking, Acting, Paused }

public enum RunEnd { Completed, Stopped, Failed, StepLimit }

/// <summary>One tool call shown in the timeline.</summary>
public sealed class AgentStep
{
    public required string Id { get; init; }
    public required string RunId { get; init; }
    public required int Index { get; init; }
    public required string Tool { get; init; }
    public required string Title { get; init; }
    public string Source { get; init; } = "assistant";
    public string Status { get; set; } = "running";
    public string? Detail { get; set; }
    public string? Thumbnail { get; set; }
    public DateTimeOffset Started { get; init; } = DateTimeOffset.Now;
    public long DurationMs { get; set; }
}

public interface IAgentObserver
{
    void OnStateChanged(AgentState state, string detail);
    /// <summary>Narration before tool calls (isFinal=false) or the final reply of a run (isFinal=true).</summary>
    void OnAssistantText(string runId, string text, bool isFinal);
    /// <summary>Raised when a step starts and again when it finishes.</summary>
    void OnStep(AgentStep step);
    void OnUsage(TokenUsage call, TokenUsage total);
    void OnRunFinished(string runId, RunEnd end, string? message);
}

public sealed class AgentOptions
{
    public required Func<IChatProvider> ProviderFactory { get; init; }
    public required Func<string> SystemPromptFactory { get; init; }
    public required IReadOnlyList<ToolDefinition> Tools { get; init; }
    public required Func<string, JsonElement, CancellationToken, Task<ToolOutcome>> RunTool { get; init; }
    /// <summary>0 means unlimited.</summary>
    public Func<int> MaxSteps { get; init; } = () => 0;
    public Func<int> KeepScreenshots { get; init; } = () => 3;
    public Func<int> MaxOutputTokens { get; init; } = () => 4096;
}

/// <summary>
/// The assistant's conversation and its tool loop. There is no step limit unless the user sets one:
/// it keeps calling tools until the model gives a final answer or the user stops it. The user can
/// pause (takeover), resume, stop, and add instructions while it works.
/// </summary>
public sealed class AgentSession
{
    private readonly AgentOptions _o;
    private readonly IAgentObserver _observer;
    private readonly List<ChatMessage> _history = new();
    private readonly ConcurrentQueue<string> _notes = new();
    private readonly PauseGate _gate = new();
    private readonly object _lock = new();
    private readonly HashSet<string> _toolNames;
    private CancellationTokenSource? _runCts;
    private Task _runTask = Task.CompletedTask;
    private volatile bool _resumeAfterTakeover;
    private volatile AgentState _state = AgentState.Idle;
    private TokenUsage _total = TokenUsage.Zero;

    public AgentSession(AgentOptions options, IAgentObserver observer)
    {
        _o = options;
        _observer = observer;
        _toolNames = options.Tools.Select(t => t.Name).ToHashSet();
    }

    public bool IsRunning => !_runTask.IsCompleted;
    public bool IsPaused => _gate.IsPaused;
    public AgentState State => _state;
    public string? CurrentRunId { get; private set; }
    public TokenUsage TotalUsage { get { lock (_lock) return _total; } }
    public int HistoryCount { get { lock (_lock) return _history.Count; } }
    internal List<ChatMessage> History => _history;

    /// <summary>Starts a run, or adds the text as extra instructions to the run in progress.</summary>
    public void Submit(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        lock (_lock)
        {
            if (IsRunning)
            {
                _notes.Enqueue(text);
                return;
            }
            AppendUserText(text);
            var runId = Ids.New("run");
            CurrentRunId = runId;
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            _gate.Resume();
            _runTask = Task.Run(() => RunAsync(runId, ct));
        }
    }

    /// <summary>Pauses before the next model call or tool. <paramref name="takeover"/> tells the model to re-check the screen.</summary>
    public void Pause(string reason, bool takeover)
    {
        if (!IsRunning) return;
        if (takeover) _resumeAfterTakeover = true;
        _gate.Pause();
        SetState(AgentState.Paused, reason);
    }

    public void Resume()
    {
        if (!_gate.IsPaused) return;
        _gate.Resume();
        if (IsRunning) SetState(AgentState.Thinking, "Continuing…");
    }

    public void Stop()
    {
        lock (_lock) _runCts?.Cancel();
        _gate.Resume();
    }

    /// <summary>Forgets the conversation. Stops a running task first.</summary>
    public async Task ResetAsync()
    {
        Stop();
        try { await _runTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        lock (_lock)
        {
            _history.Clear();
            while (_notes.TryDequeue(out _)) { }
            _total = TokenUsage.Zero;
        }
    }

    public Task WaitForIdleAsync(TimeSpan timeout) => _runTask.WaitAsync(timeout);

    private void AppendUserText(string text)
    {
        if (_history.Count > 0 && _history[^1].Role == ChatRole.User) _history[^1].Parts.Add(new TextPart(text));
        else _history.Add(ChatMessage.UserText(text));
    }

    private void SetState(AgentState state, string detail)
    {
        _state = state;
        Safe(() => _observer.OnStateChanged(state, detail));
    }

    private static void Safe(Action a)
    {
        try { a(); } catch (Exception e) { Debug.WriteLine("Observer error: " + e); }
    }

    private async Task WaitIfPausedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
    }

    private void DrainNotes()
    {
        lock (_lock)
        {
            if (_resumeAfterTakeover)
            {
                _resumeAfterTakeover = false;
                AppendUserText("[The user paused you to use the PC themselves and has now let you continue. The screen may have changed: look again before acting.]");
            }
            while (_notes.TryDequeue(out var note))
                AppendUserText("The user added while you were working: " + note);
        }
    }

    private async Task RunAsync(string runId, CancellationToken ct)
    {
        var end = RunEnd.Completed;
        string? message = null;
        int steps = 0;
        string system = "";
        try
        {
            system = _o.SystemPromptFactory();
            bool compacted = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(ct).ConfigureAwait(false);
                DrainNotes();
                lock (_lock)
                {
                    ContextPruner.PruneImages(_history, Math.Max(1, _o.KeepScreenshots()));
                    ContextPruner.TruncateOldToolText(_history);
                }

                SetState(AgentState.Thinking, "Thinking…");
                var provider = _o.ProviderFactory();
                ChatResult result;
                try
                {
                    result = await provider.CompleteAsync(new ChatRequest
                    {
                        SystemPrompt = system,
                        Messages = _history,
                        Tools = _o.Tools,
                        MaxOutputTokens = _o.MaxOutputTokens(),
                    }, ct).ConfigureAwait(false);
                    compacted = false;
                }
                catch (ProviderException e) when (e.IsContextTooLong && !compacted)
                {
                    lock (_lock) compacted = ContextPruner.Compact(_history);
                    if (!compacted) throw;
                    continue;
                }

                TokenUsage total;
                lock (_lock) total = _total = _total.Add(result.Usage);
                Safe(() => _observer.OnUsage(result.Usage, total));

                var parts = result.Parts.Where(p => p is not TextPart t || t.Text.Length > 0).ToList();
                if (parts.Count == 0)
                {
                    var why = result.StopReason is "max_tokens" or "length" or "max_output_tokens" ? "(The reply was cut off.)" : "(No reply.)";
                    parts.Add(new TextPart(why));
                }
                var assistant = new ChatMessage(ChatRole.Assistant, parts);
                lock (_lock) _history.Add(assistant);
                var calls = assistant.ToolCalls.ToList();
                var text = assistant.Text;

                if (calls.Count == 0)
                {
                    Safe(() => _observer.OnAssistantText(runId, text, true));
                    if (!_notes.IsEmpty && !ct.IsCancellationRequested) continue; // the user added more while it answered
                    break;
                }
                if (text.Length > 0) Safe(() => _observer.OnAssistantText(runId, text, false));

                var results = new List<ContentPart>();
                bool skipRest = false;
                foreach (var call in calls)
                {
                    if (ct.IsCancellationRequested)
                    {
                        results.Add(Result(call, "Not run: the user stopped the task.", true));
                        continue;
                    }
                    if (skipRest)
                    {
                        results.Add(Result(call, "Skipped because the user paused you. Look at the screen again before continuing.", true));
                        continue;
                    }
                    int max = _o.MaxSteps();
                    if (max > 0 && steps >= max)
                    {
                        results.Add(Result(call, "Not run: the step limit set by the user was reached.", true));
                        end = RunEnd.StepLimit;
                        skipRest = true;
                        continue;
                    }
                    if (_gate.IsPaused)
                    {
                        // The user took over between two calls: wait, then re-plan from a fresh look.
                        try { await WaitIfPausedAsync(ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { results.Add(Result(call, "Not run: the user stopped the task.", true)); continue; }
                        skipRest = true;
                        results.Add(Result(call, "Skipped because the user paused you. Look at the screen again before continuing.", true));
                        continue;
                    }

                    steps++;
                    var step = new AgentStep
                    {
                        Id = Ids.New("step"),
                        RunId = runId,
                        Index = steps,
                        Tool = call.Name,
                        Title = StepTitles.Describe(call.Name, call.Arguments),
                    };
                    Safe(() => _observer.OnStep(step));
                    SetState(AgentState.Acting, step.Title);
                    var watch = Stopwatch.StartNew();
                    var outcome = await RunOneAsync(call, ct).ConfigureAwait(false);
                    step.DurationMs = watch.ElapsedMilliseconds;
                    step.Status = outcome.Interrupted ? "interrupted" : outcome.Denied ? "denied" : outcome.IsError ? "error" : "done";
                    step.Detail = outcome.Summary;
                    step.Thumbnail = outcome.Thumbnail;
                    Safe(() => _observer.OnStep(step));
                    results.Add(new ToolResultPart(call.Id, call.Name, outcome.Content.ToList(), outcome.IsError));
                    if (outcome.Interrupted) skipRest = true;
                }
                lock (_lock) _history.Add(new ChatMessage(ChatRole.User, results));

                if (ct.IsCancellationRequested) { end = RunEnd.Stopped; break; }
                if (end == RunEnd.StepLimit) { message = $"Stopped after {steps} steps because of the step limit in Settings."; break; }
            }
        }
        catch (OperationCanceledException)
        {
            end = RunEnd.Stopped;
        }
        catch (ProviderException e)
        {
            end = RunEnd.Failed;
            message = e.Friendly;
        }
        catch (Exception e)
        {
            end = RunEnd.Failed;
            message = "Something went wrong: " + e.Message;
        }
        finally
        {
            lock (_lock) PairDanglingToolCalls();
            _gate.Resume();
            SetState(AgentState.Idle, end == RunEnd.Completed ? "Done" : end == RunEnd.Stopped ? "Stopped" : "Ready");
            Safe(() => _observer.OnRunFinished(runId, end, message));
        }
    }

    private async Task<ToolOutcome> RunOneAsync(ToolCallPart call, CancellationToken ct)
    {
        if (call.ParseError is not null) return ToolOutcome.Fail(call.ParseError + " Send the arguments as a JSON object.");
        if (!_toolNames.Contains(call.Name)) return ToolOutcome.Fail($"Unknown tool: {call.Name}. Use one of the windows_* tools.");
        try
        {
            using var doc = JsonDocument.Parse(call.Arguments.ToJsonString());
            return await _o.RunTool(call.Name, doc.RootElement.Clone(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ToolOutcome.Fail("Stopped by the user.", interrupted: true);
        }
        catch (Exception e)
        {
            return ToolOutcome.Fail("Tool error: " + e.Message);
        }
    }

    private static ToolResultPart Result(ToolCallPart call, string text, bool isError)
        => new(call.Id, call.Name, [new TextPart(text)], isError);

    /// <summary>If a run ended between a tool call and its result, add results so the history stays valid.</summary>
    private void PairDanglingToolCalls()
    {
        if (_history.Count == 0 || _history[^1].Role != ChatRole.Assistant) return;
        var calls = _history[^1].ToolCalls.ToList();
        if (calls.Count == 0) return;
        _history.Add(new ChatMessage(ChatRole.User, calls.Select(c => (ContentPart)Result(c, "Not run: the user stopped the task.", true))));
    }
}
