using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PocketAgent.Core.App;
using PocketAgent.Core.Mcp;
using PocketAgent.Core.Safety;
using PocketAgent.Core.Settings;
using PocketAgent.Core.Tools;
using PocketAgent.Core.Tunnel;

namespace PocketAgent.Core.Tests;

/// <summary>The whole app controller with fake Windows services and a fake Claude endpoint.</summary>
public sealed class ControllerTests : IAsyncDisposable
{
    private FakeHttpServer? _server;
    private AppController? _app;
    private FakePlatform _platform = null!;
    private FakeTools _tools = null!;

    /// <summary>
    /// Starts a controller whose Anthropic preset points at a scripted fake server.
    /// The script maps (request index) → response JSON.
    /// </summary>
    private async Task<AppController> Start(Func<int, string, string> script, Action<AppSettings>? configure = null)
    {
        _server = await FakeHttpServer.StartAsync((ctx, body, i) =>
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(script(i, body));
        });
        var dir = TempDir.New();
        _platform = new FakePlatform(dir);
        var settings = new SettingsStore(dir, _platform.Protector);
        settings.Update(s =>
        {
            s.ProviderId = "anthropic";
            var c = s.ProviderConfigFor("anthropic");
            c.BaseUrl = _server.BaseUrl;
            c.ApiKeyProtected = settings.Protect("sk-ant-secret-9876");
            s.McpServerEnabled = false;
            s.ClickPreview = true;
            configure?.Invoke(s);
        });
        var approvals = new ApprovalStore(Path.Combine(dir, "approvals"));
        _tools = new FakeTools();
        _app = new AppController(_platform, settings, approvals, services => { _tools.Services = services; return _tools; });
        _app.Initialize();
        return _app;
    }

    private static string ToolUse(string name, string inputJson, string text = "")
    {
        var content = new JsonArray();
        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_" + Guid.NewGuid().ToString("N"), ["name"] = name, ["input"] = JsonNode.Parse(inputJson) });
        return new JsonObject
        {
            ["content"] = content,
            ["stop_reason"] = "tool_use",
            ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 10 },
        }.ToJsonString();
    }

    private static string Final(string text) => new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["stop_reason"] = "end_turn",
        ["usage"] = new JsonObject { ["input_tokens"] = 50, ["output_tokens"] = 5 },
    }.ToJsonString();

    private JsonNode? Prompt() => _platform.UiFake.LastState?["prompts"]?.AsArray().FirstOrDefault();

    [Test]
    public async Task TypedRequestTurnsControlOnAsksForApprovalAndSpeaksTheReply()
    {
        var app = await Start((i, body) => i == 0 ? ToolUse("windows_tap", "{\"element\":3}", "Clicking it.") : Final("Clicked it."));
        _tools.Handler = (s, name, args) =>
        {
            s.RequireApproval(@"exe:c:\apps\editor.exe", "editor.exe", @"C:\apps\editor.exe");
            s.PreviewClick(10, 20);
            return ToolOutcome.Ok("Clicked (10,20).");
        };
        app.HandleUiMessage("{\"type\":\"chat.send\",\"text\":\"click the third thing\"}");

        await Assert.Eventually(() => Prompt() is not null, "approval prompt shown");
        Assert.True(app.ControlActive, "control switched on automatically");
        Assert.Equal("prompt", _platform.OverlayFake.Last!.Mode, "bubble shows the prompt");
        var id = Prompt()!["id"]!.GetValue<string>();
        app.HandleUiMessage($"{{\"type\":\"prompt.answer\",\"id\":\"{id}\",\"choice\":\"session\"}}");

        await Assert.Eventually(() => _platform.TtsFake.Spoken.Contains("Clicked it."), "reply spoken");
        await Assert.Eventually(() => !app.Agent.IsRunning, "run finished");
        var chat = _platform.UiFake.Of("chat").Select(c => c["text"]!.GetValue<string>()).ToList();
        Assert.True(chat.Contains("click the third thing"), "user message");
        Assert.True(chat.Contains("Clicked it."), "assistant reply");
        Assert.True(_platform.TtsFake.Spoken.Contains("Clicking it."), "narration spoken");
        Assert.True(app.Approvals.IsAllowed(@"exe:c:\apps\editor.exe"), "allowed for the session");
        Assert.Equal(1, _platform.OverlayFake.Previews.Count, "click preview shown");
        var steps = _platform.UiFake.Of("step").ToList();
        Assert.Equal("done", steps.Last()["status"]!.GetValue<string>());
        // The model was given the windows tools and the conversation.
        var first = JsonNode.Parse(_server!.Requests[0].Body)!;
        Assert.True(first["tools"]!.AsArray().Count >= 19, "tools sent");
        Assert.Contains("Desktop control is ON", first["system"]![0]!["text"]!.GetValue<string>());
    }

    [Test]
    public async Task RiskyClickDeclinedInTheBubbleIsNotPerformed()
    {
        var app = await Start((i, body) => i == 0 ? ToolUse("windows_tap", "{\"element\":1}") : Final("Okay, I didn't send it."));
        bool clicked = false;
        _tools.Handler = (s, name, args) =>
        {
            s.ConfirmRisk("click “Send”", "outlook.exe", "This usually sends something.");
            clicked = true;
            return ToolOutcome.Ok("clicked");
        };
        app.HandleUiMessage("{\"type\":\"chat.send\",\"text\":\"send the mail\"}");
        await Assert.Eventually(() => _platform.OverlayFake.Last?.Prompt is not null, "risk prompt in bubble");
        var prompt = _platform.OverlayFake.Last!.Prompt!;
        Assert.Equal("risk", prompt.Kind);
        _platform.OverlayFake.Fire($"answer:{prompt.Id}:deny");
        await Assert.Eventually(() => !app.Agent.IsRunning, "run finished");
        Assert.False(clicked, "the click never happened");
        var step = _platform.UiFake.Of("step").Last();
        Assert.Equal("denied", step["status"]!.GetValue<string>());
        var secondRequest = _server!.Requests[1].Body;
        Assert.Contains("Do not retry", secondRequest);
    }

    [Test]
    public async Task VoiceRequestGoesFromTalkKeyToTheAssistant()
    {
        var app = await Start((i, body) => Final("Notepad is open."));
        _platform.LiveFake.NextText = "open notepad";
        app.OnTalkKey(down: true);
        Assert.True(_platform.RecorderFake.IsRecording, "recording");
        Assert.Equal("listening", _platform.OverlayFake.Last?.Mode ?? (await WaitMode("listening")));
        _platform.RecorderFake.Feed(900);
        app.OnTalkKey(down: false);
        await Assert.Eventually(() => _platform.UiFake.Of("chat").Any(c => c["text"]!.GetValue<string>() == "open notepad" && c["source"]?.GetValue<string>() == "voice"), "voice text in chat");
        await Assert.Eventually(() => _platform.TtsFake.Spoken.Contains("Notepad is open."), "spoken reply");
        Assert.True(_platform.Cues.Contains("listen"), "listening cue");
    }

    private async Task<string> WaitMode(string mode)
    {
        await Assert.Eventually(() => _platform.OverlayFake.Last?.Mode == mode, "overlay " + mode);
        return mode;
    }

    [Test]
    public async Task TooShortTalkKeyPressIsIgnored()
    {
        var app = await Start((i, body) => Final("x"));
        app.OnTalkKey(true);
        _platform.RecorderFake.Feed(100);
        app.OnTalkKey(false);
        await Task.Delay(150);
        Assert.Equal(0, _server!.Requests.Count, "nothing sent");
        Assert.Contains("Hold the talk key", _platform.OverlayFake.Last?.Title);
    }

    [Test]
    public async Task SayingStopEndsTheRunningTask()
    {
        var release = new ManualResetEventSlim(false);
        var app = await Start((i, body) => ToolUse("windows_wait", "{\"seconds\":5}"));
        _tools.Handler = (s, n, a) =>
        {
            for (int k = 0; k < 200 && !s.InterruptRequested && !release.IsSet; k++) Thread.Sleep(10);
            return ToolOutcome.Ok("waited");
        };
        app.HandleUiMessage("{\"type\":\"chat.send\",\"text\":\"wait a lot\"}");
        await Assert.Eventually(() => app.Agent.IsRunning && _tools.Calls.Count > 0, "task running");
        _platform.LiveFake.NextText = "Stop!";
        app.OnTalkKey(true);
        _platform.RecorderFake.Feed(600);
        app.OnTalkKey(false);
        release.Set();
        await Assert.Eventually(() => !app.Agent.IsRunning, "task stopped", 8000);
        Assert.True(_platform.UiFake.Of("chat").Any(c => c["text"]!.GetValue<string>() == "Stopped."), "stopped note");
        Assert.True(app.ControlActive, "control stays on after a voice stop");
    }

    [Test]
    public async Task UserInputPausesAutomationUntilResumed()
    {
        var app = await Start((i, body) => i < 30 ? ToolUse("windows_status", "{}") : Final("done"));
        _tools.Handler = (s, n, a) => { Thread.Sleep(30); return ToolOutcome.Ok("ok"); };
        app.HandleUiMessage("{\"type\":\"chat.send\",\"text\":\"keep checking\"}");
        await Assert.Eventually(() => _tools.Calls.Count >= 2, "working");
        app.OnUserInput();
        await Assert.Eventually(() => _platform.OverlayFake.Last?.Mode == "paused", "bubble paused");
        var callsAtPause = _tools.Calls.Count;
        await Task.Delay(300);
        Assert.True(_tools.Calls.Count <= callsAtPause + 1, "no more actions while paused");
        Assert.True(_platform.UiFake.LastState!["agent"]!["paused"]!.GetValue<bool>(), "state says paused");
        _platform.OverlayFake.Fire("resume");
        await Assert.Eventually(() => _tools.Calls.Count > callsAtPause + 1, "continues after resume");
        app.StopTask();
        await Assert.Eventually(() => !app.Agent.IsRunning, "stopped");
        var sent = string.Join("\n", _server!.Requests.Select(r => r.Body));
        Assert.Contains("paused you to use the PC", sent);
    }

    [Test]
    public async Task StateNeverLeaksKeysOrTheSecret()
    {
        var app = await Start((i, body) => Final("ok"));
        await app.StartControlAsync();
        app.HandleUiMessage("{\"type\":\"ready\"}");
        await Assert.Eventually(() => _platform.UiFake.LastState is not null, "state");
        await Task.Delay(120);
        var all = _platform.UiFake.AllJson;
        Assert.DoesNotContain("sk-ant-secret-9876", all, "API key");
        Assert.DoesNotContain(app.Secret, all, "MCP secret");
        Assert.Equal("••••9876", _platform.UiFake.LastState!["settings"]!["providers"]!["anthropic"]!["keyHint"]!.GetValue<string>());
        app.HandleUiMessage("{\"type\":\"secret.copy\",\"which\":\"cursor\"}");
        Assert.Contains(app.Secret, _platform.Clipboard, "copy puts the real URL on the clipboard");
        Assert.Contains("mcpServers", _platform.Clipboard);
        app.HandleUiMessage("{\"type\":\"settings.update\",\"patch\":{\"providers\":{\"anthropic\":{\"apiKeyProtected\":\"x\"}}}}");
        Assert.Equal("sk-ant-secret-9876", app.Settings.Reveal(app.Settings.Current.Providers["anthropic"].ApiKeyProtected), "UI cannot overwrite keys via patch");
        app.HandleUiMessage("{\"type\":\"settings.setKey\",\"target\":\"provider:anthropic\",\"key\":\"sk-new-1111\"}");
        Assert.Equal("sk-new-1111", app.Settings.Reveal(app.Settings.Current.Providers["anthropic"].ApiKeyProtected));
    }

    [Test]
    public async Task MissingKeyIsExplainedInsteadOfFailing()
    {
        var app = await Start((i, body) => Final("x"), s => s.ProviderConfigFor("anthropic").ApiKeyProtected = "");
        await app.SubmitAsync("hello", "typed");
        Assert.True(_platform.UiFake.Of("chat").Any(c => c["role"]!.GetValue<string>() == "system" && c["text"]!.GetValue<string>().Contains("API key")), "explained");
        Assert.False(app.ControlActive, "control not started for nothing");
    }

    [Test]
    public async Task ApprovalTimeoutAndStopControlDenyPendingPrompts()
    {
        var app = await Start((i, body) => i == 0 ? ToolUse("windows_tap", "{\"x\":1,\"y\":2}") : Final("ok"));
        Exception? seen = null;
        _tools.Handler = (s, n, a) =>
        {
            try { s.RequireApproval("exe:c:\\x.exe", "x.exe", "x"); }
            catch (ToolDeniedException e) { seen = e; throw; }
            return ToolOutcome.Ok("no");
        };
        app.HandleUiMessage("{\"type\":\"chat.send\",\"text\":\"do\"}");
        await Assert.Eventually(() => Prompt() is not null, "prompt");
        await app.StopControlAsync("test");
        await Assert.Eventually(() => seen is not null, "denied when control stopped");
        Assert.Contains("switched off", seen!.Message);
    }

    [Test]
    public async Task BlockedAnswerAddsTheAppToTheNeverAllowList()
    {
        var app = await Start((i, body) => i == 0 ? ToolUse("windows_get_screen", "{}") : Final("ok"));
        _tools.Handler = (s, n, a) => { s.RequireApproval(@"exe:c:\bank\bank.exe", "bank.exe", "x"); return ToolOutcome.Ok("read"); };
        app.HandleUiMessage("{\"type\":\"chat.send\",\"text\":\"read my bank\"}");
        await Assert.Eventually(() => Prompt() is not null, "prompt");
        app.AnswerPrompt(Prompt()!["id"]!.GetValue<string>(), "block");
        await Assert.Eventually(() => !app.Agent.IsRunning, "finished");
        Assert.True(app.Approvals.IsBlocked(@"C:\bank\bank.exe"), "blocked");
    }

    [Test]
    public async Task WakeWordStartsListeningAndSilenceEndsIt()
    {
        var app = await Start((i, body) => Final("Here you go."), s => s.WakeWordEnabled = true);
        Assert.True(_platform.WakeFake.WakeWord, "wake word configured");
        _platform.LiveFake.NextText = "what time is it";
        _platform.WakeFake.Wake();
        Assert.True(_platform.RecorderFake.IsRecording, "listening after wake word");
        // quiet calibration, speech, then silence → voice activity detection ends the recording
        _platform.RecorderFake.Feed(300, 0.001);
        for (int k = 0; k < 10; k++) _platform.RecorderFake.Feed(100, 0.3);
        for (int k = 0; k < 15; k++) _platform.RecorderFake.Feed(100, 0.001);
        await Assert.Eventually(() => !_platform.RecorderFake.IsRecording, "stopped by silence");
        await Assert.Eventually(() => _platform.TtsFake.Spoken.Contains("Here you go."), "answered");
    }

    [Test]
    public async Task ControlStartsTheRustMcpServerAndStopsIt()
    {
        try { RustEngine.EnsureLoaded(); }
        catch (DllNotFoundException) { throw new AssertException("Native Rust engine not built"); }
        int port = FakeHttpServer.FreePort();
        var app = await Start((i, body) => Final("x"), s => { s.McpServerEnabled = true; s.Port = port; });
        _tools.Handler = (s, n, a) => ToolOutcome.Ok("Windows desktop: unlocked.");
        await app.StartControlAsync();
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{port}/k/{app.Secret}/mcp";
        var response = await http.PostAsync(url, new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"windows_status\"}}", Encoding.UTF8, "application/json"));
        Assert.Contains("unlocked", await response.Content.ReadAsStringAsync());
        await Assert.Eventually(() => _platform.UiFake.Of("step").Any(s => s["source"]!.GetValue<string>() == "mcp" && s["status"]!.GetValue<string>() == "done"), "external call in timeline");
        await app.StopControlAsync();
        bool refused;
        try { refused = (await http.PostAsync(url, new StringContent("{}"))).StatusCode != HttpStatusCode.OK; }
        catch (HttpRequestException) { refused = true; }
        Assert.True(refused, "server gone after stop");
    }

    [Test]
    public async Task ExtendAndUnlimitedAutoStop()
    {
        var app = await Start((i, body) => Final("x"));
        await app.StartControlAsync();
        await Task.Delay(100);
        var before = _platform.UiFake.LastState!["control"]!["expiresAt"]!.GetValue<long>();
        app.HandleUiMessage("{\"type\":\"control.extend\",\"minutes\":10}");
        await Task.Delay(120);
        var after = _platform.UiFake.LastState!["control"]!["expiresAt"]!.GetValue<long>();
        Assert.True(after - before >= 9 * 60_000, "extended by ~10 minutes");
        app.HandleUiMessage("{\"type\":\"settings.update\",\"patch\":{\"autoStopMinutes\":0}}");
        await Task.Delay(120);
        Assert.True(_platform.UiFake.LastState!["control"]!["expiresAt"] is null, "until I stop it");
    }

    [Test]
    public async Task TunnelReportsThePublicAddress()
    {
        if (OperatingSystem.IsWindows()) return; // uses a shell script as a stand-in for cloudflared
        var dir = TempDir.New();
        var fake = Path.Combine(dir, "cloudflared");
        File.WriteAllText(fake, "#!/bin/sh\necho 'INF Requesting new quick Tunnel on trycloudflare.com...' 1>&2\necho 'INF |  https://brave-otter-42.trycloudflare.com  |' 1>&2\nsleep 30\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var tunnel = new TunnelManager((c, m) => { });
        await tunnel.StartAsync(fake, 8765, CancellationToken.None, TimeSpan.FromSeconds(10));
        Assert.Equal(TunnelStatus.Online, tunnel.Status);
        Assert.Equal("https://brave-otter-42.trycloudflare.com", tunnel.PublicUrl);
        tunnel.Stop();
        Assert.Equal(TunnelStatus.Off, tunnel.Status);
        Assert.Equal(fake, TunnelManager.Find(fake, dir));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
    }
}
