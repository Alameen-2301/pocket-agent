using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Providers;
using PocketAgent.Core.Tools;

namespace PocketAgent.Core.Tests;

/// <summary>A provider that answers from a script and records what it was sent.</summary>
public sealed class ScriptedProvider : IChatProvider
{
    private readonly Func<ChatRequest, int, ChatResult> _script;
    private int _calls;
    public ScriptedProvider(Func<ChatRequest, int, ChatResult> script) { _script = script; }
    public string DisplayName => "Scripted";
    public string Model => "scripted-1";
    public int Calls => _calls;
    public ConcurrentQueue<List<ChatMessage>> Seen { get; } = new();
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        Seen.Enqueue(request.Messages.Select(m => new ChatMessage(m.Role, m.Parts.ToList())).ToList());
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        ct.ThrowIfCancellationRequested();
        return _script(request, Interlocked.Increment(ref _calls) - 1);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["scripted-1"]);

    public static ChatResult Tool(string name, string argsJson, string? text = null)
    {
        var r = new ChatResult { StopReason = "tool_use", Usage = new TokenUsage(10, 5) };
        if (text is not null) r.Parts.Add(new TextPart(text));
        r.Parts.Add(new ToolCallPart("call_" + Guid.NewGuid().ToString("N")[..6], name, JsonNode.Parse(argsJson)!.AsObject()));
        return r;
    }

    public static ChatResult Final(string text)
    {
        var r = new ChatResult { StopReason = "end_turn", Usage = new TokenUsage(10, 5) };
        r.Parts.Add(new TextPart(text));
        return r;
    }
}

public sealed class RecordingObserver : IAgentObserver
{
    public ConcurrentQueue<string> Events { get; } = new();
    public ConcurrentQueue<(string Text, bool Final)> Texts { get; } = new();
    public ConcurrentQueue<AgentStep> Steps { get; } = new();
    public TaskCompletionSource<(RunEnd End, string? Message)> Finished { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Reset() => Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void OnStateChanged(AgentState state, string detail) => Events.Enqueue("state:" + state);
    public void OnAssistantText(string runId, string text, bool isFinal) => Texts.Enqueue((text, isFinal));
    public void OnStep(AgentStep step) { if (step.Status != "running") Steps.Enqueue(step); }
    public void OnUsage(TokenUsage call, TokenUsage total) => Events.Enqueue("usage:" + total.InputTokens);
    public void OnRunFinished(string runId, RunEnd end, string? message) => Finished.TrySetResult((end, message));
}

public static class AgentTests
{
    private static AgentSession Session(ScriptedProvider provider, RecordingObserver obs, Func<string, JsonElement, CancellationToken, Task<ToolOutcome>>? tool = null, int maxSteps = 0)
        => new(new AgentOptions
        {
            ProviderFactory = () => provider,
            SystemPromptFactory = () => "system",
            Tools = ToolCatalog.All,
            RunTool = tool ?? ((name, args, ct) => Task.FromResult(ToolOutcome.Ok($"{name} ok"))),
            MaxSteps = () => maxSteps,
        }, obs);

    [Test]
    public static async Task RunsToolsUntilTheModelAnswers()
    {
        var provider = new ScriptedProvider((req, i) => i < 3
            ? ScriptedProvider.Tool("windows_status", "{}", i == 0 ? "Checking the screen." : null)
            : ScriptedProvider.Final("All done."));
        var obs = new RecordingObserver();
        var agent = Session(provider, obs);
        agent.Submit("do it");
        var (end, _) = await obs.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunEnd.Completed, end);
        Assert.Equal(4, provider.Calls);
        Assert.Equal(3, obs.Steps.Count);
        Assert.Equal("Checking the screen.", obs.Texts.First().Text);
        Assert.Equal(false, obs.Texts.First().Final);
        Assert.Equal(("All done.", true), obs.Texts.Last());
        Assert.Equal(40L, agent.TotalUsage.InputTokens);
        // Every tool call got a result in the next message.
        var last = provider.Seen.Last();
        for (int k = 0; k < last.Count; k++)
            foreach (var call in last[k].ToolCalls)
                Assert.True(last[k + 1].ToolResults.Any(r => r.ToolCallId == call.Id), "paired " + call.Id);
    }

    [Test]
    public static async Task HasNoStepLimitByDefault()
    {
        var provider = new ScriptedProvider((req, i) => i < 60 ? ScriptedProvider.Tool("windows_wait", "{\"seconds\":0.2}") : ScriptedProvider.Final("Finished a long task."));
        var obs = new RecordingObserver();
        var agent = Session(provider, obs);
        agent.Submit("long task");
        var (end, _) = await obs.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(RunEnd.Completed, end);
        Assert.Equal(60, obs.Steps.Count, "60 steps ran");
    }

    [Test]
    public static async Task OptionalStepLimitStopsTheRun()
    {
        var provider = new ScriptedProvider((req, i) => ScriptedProvider.Tool("windows_status", "{}"));
        var obs = new RecordingObserver();
        var agent = Session(provider, obs, maxSteps: 3);
        agent.Submit("loop");
        var (end, message) = await obs.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunEnd.StepLimit, end);
        Assert.Equal(3, obs.Steps.Count);
        Assert.Contains("step limit", message);
    }

    [Test]
    public static async Task StopEndsTheRunAndKeepsHistoryValid()
    {
        var started = new TaskCompletionSource();
        var provider = new ScriptedProvider((req, i) => ScriptedProvider.Tool("windows_wait", "{\"seconds\":5}"));
        var obs = new RecordingObserver();
        AgentSession? agent = null;
        agent = Session(provider, obs, async (name, args, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolOutcome.Ok("never");
        });
        agent.Submit("wait forever");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        agent.Stop();
        var (end, _) = await obs.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunEnd.Stopped, end);
        var h = agent.History;
        Assert.Equal(ChatRole.User, h[^1].Role, "results appended");
        Assert.True(h[^1].ToolResults.Any(), "tool call has a result");
        // A follow-up starts a fresh run on the same conversation.
        obs.Reset();
        var provider2Calls = provider.Calls;
        agent.Submit("continue");
        await Task.Delay(200);
        agent.Stop();
        await obs.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(provider.Calls > provider2Calls, "second run called the model");
    }

    [Test]
    public static async Task NotesAddedWhileWorkingReachTheModel()
    {
        var gate = new TaskCompletionSource();
        var provider = new ScriptedProvider((req, i) =>
        {
            if (i == 0) return ScriptedProvider.Tool("windows_status", "{}");
            var text = string.Join(" | ", req.Messages.Select(m => m.Text));
            return text.Contains("also open the calculator") ? ScriptedProvider.Final("Both done.") : ScriptedProvider.Tool("windows_status", "{}");
        });
        var obs = new RecordingObserver();
        var agent = Session(provider, obs, async (name, args, ct) =>
        {
            await gate.Task;
            return ToolOutcome.Ok("ok");
        });
        agent.Submit("open notepad");
        await Assert.Eventually(() => agent.State == AgentState.Acting, "acting");
        agent.Submit("also open the calculator");
        gate.SetResult();
        var (end, _) = await obs.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunEnd.Completed, end);
        Assert.Equal("Both done.", obs.Texts.Last().Text);
    }

    [Test]
    public static async Task TakeoverPauseSkipsStaleCallsAndAsksForAFreshLook()
    {
        int toolRuns = 0;
        var provider = new ScriptedProvider((req, i) =>
        {
            if (i == 0)
            {
                var r = ScriptedProvider.Tool("windows_tap", "{\"element\":1}");
                r.Parts.Add(new ToolCallPart("call_b", "windows_tap", new JsonObject { ["element"] = 2 }));
                return r;
            }
            return ScriptedProvider.Final("Done after resume.");
        });
        var obs = new RecordingObserver();
        AgentSession agent = null!;
        agent = Session(provider, obs, (name, args, ct) =>
        {
            Interlocked.Increment(ref toolRuns);
            agent.Pause("You took over", takeover: true); // the user grabs the mouse during the first click
            return Task.FromResult(ToolOutcome.Fail("Interrupted: the user took over.", interrupted: true));
        });
        agent.Submit("click two things");
        await Assert.Eventually(() => agent.IsPaused, "paused");
        await Task.Delay(100);
        Assert.Equal(1, toolRuns, "second click skipped");
        agent.Resume();
        var (end, _) = await obs.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunEnd.Completed, end);
        var sent = string.Join("\n", provider.Seen.Last().Select(m => m.Text + string.Join(" ", m.ToolResults.Select(r => r.Text))));
        Assert.Contains("Skipped because the user paused you", sent);
        Assert.Contains("look again before acting", sent);
    }

    [Test]
    public static async Task ContextTooLongTriggersCompactionOnce()
    {
        int failures = 0;
        var provider = new ScriptedProvider((req, i) =>
        {
            if (i < 12) return ScriptedProvider.Tool("windows_screenshot", "{}");
            if (failures++ == 0) throw new ProviderException("prompt is too long", 400);
            return ScriptedProvider.Final("Recovered.");
        });
        var obs = new RecordingObserver();
        var agent = Session(provider, obs, (n, a, ct) =>
        {
            var o = ToolOutcome.Ok("shot");
            o.Content.Add(new ImagePart("image/jpeg", "QUJD"));
            return Task.FromResult(o);
        });
        agent.Submit("lots of screenshots");
        var (end, _) = await obs.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunEnd.Completed, end);
        Assert.Equal("Recovered.", obs.Texts.Last().Text);
        Assert.True(agent.History.Count < 26, "history compacted: " + agent.History.Count);
    }

    [Test]
    public static async Task ProviderErrorsEndTheRunWithAFriendlyMessage()
    {
        var provider = new ScriptedProvider((req, i) => throw new ProviderException("AI provider error 401", 401));
        var obs = new RecordingObserver();
        var agent = Session(provider, obs);
        agent.Submit("hi");
        var (end, message) = await obs.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunEnd.Failed, end);
        Assert.Contains("API key", message);
    }

    [Test]
    public static async Task UnknownToolsAndBadArgumentsAreReportedToTheModel()
    {
        var provider = new ScriptedProvider((req, i) =>
        {
            if (i == 0)
            {
                var r = new ChatResult();
                r.Parts.Add(new ToolCallPart("c1", "format_disk", new JsonObject()));
                r.Parts.Add(new ToolCallPart("c2", "windows_tap", new JsonObject(), "Invalid JSON arguments: x"));
                return r;
            }
            return ScriptedProvider.Final("ok");
        });
        var obs = new RecordingObserver();
        var agent = Session(provider, obs);
        agent.Submit("go");
        await obs.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var results = provider.Seen.Last().SelectMany(m => m.ToolResults).ToList();
        Assert.Contains("Unknown tool", results[0].Text);
        Assert.True(results[0].IsError, "error");
        Assert.Contains("Invalid JSON", results[1].Text);
    }
}
