using System.Text.Json;
using System.Text.Json.Nodes;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Providers;
using PocketAgent.Core.Safety;
using PocketAgent.Core.Settings;
using PocketAgent.Core.Tools;
using PocketAgent.Core.Tunnel;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.App;

public sealed partial class AppController
{
    public void Toast(string kind, string message) => Post("toast", new { kind, message });

    /// <summary>Entry point for every message from the HTML interface.</summary>
    public void HandleUiMessage(string json)
    {
        JsonObject msg;
        try { msg = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException) { return; }
        var type = msg.Str("type") ?? "";
        try { Dispatch(type, msg); }
        catch (Exception e)
        {
            Log("error", $"UI action '{type}' failed: {e.Message}");
            Toast("error", e.Message);
        }
    }

    private void Dispatch(string type, JsonObject m)
    {
        switch (type)
        {
            case "ready":
                RequestPush();
                SendHistory();
                break;
            case "control.start": _ = StartControlAsync(); break;
            case "control.stop": _ = StopControlAsync(); break;
            case "control.toggle": _ = _controlActive ? StopControlAsync() : StartControlAsync(); break;
            case "control.extend": ExtendControl((int)(m.Num("minutes") ?? 10)); break;
            case "chat.send": _ = SubmitAsync(m.Str("text") ?? "", "typed"); break;
            case "chat.new": _ = NewConversationAsync(); break;
            case "agent.pause": PauseAutomation("Paused. Press Resume to continue."); break;
            case "agent.resume": ResumeAutomation(); break;
            case "agent.stop": StopTask(); break;
            case "voice.toggle": ToggleListening("app"); break;
            case "voice.testListen": ToggleListening("test", test: true); break;
            case "voice.testSpeak": Speak(m.Str("text") ?? "Hi! This is how I sound. Hold the talk key whenever you want me to do something.", "test"); break;
            case "voice.stopSpeaking": CancelSpeech(); RequestPush(); break;
            case "voice.micTest": SetMicTest(m.Bool("on") ?? false); break;
            case "prompt.answer": AnswerPrompt(m.Str("id") ?? "", m.Str("choice") ?? "deny"); break;
            case "settings.update":
                if (m["patch"] is JsonObject patch) _settings.ApplyPatch(patch);
                break;
            case "settings.setKey": SetKey(m.Str("target") ?? "", m.Str("key") ?? ""); break;
            case "models.load": _ = LoadModelsAsync(m.Str("providerId") ?? _settings.Current.ProviderId); break;
            case "provider.test": _ = TestProviderAsync(m.Str("providerId") ?? _settings.Current.ProviderId); break;
            case "approvals.revoke": _approvals.Revoke(m.Str("key") ?? ""); break;
            case "approvals.block":
                var name = m.Str("name") ?? "";
                if (name.Trim().Length > 0) Toast("success", "Blocked " + _approvals.Block(name));
                break;
            case "approvals.unblock": _approvals.Unblock(m.Str("key") ?? ""); break;
            case "secret.copy": CopySecret(m.Str("which") ?? "local"); break;
            case "secret.reveal": Post("secret", new { local = _controlActive ? LocalUrl : "", @public = PublicUrl ?? "" }); break;
            case "secret.rotate": RotateSecret(); break;
            case "tunnel.start": _ = StartTunnelAsync(); break;
            case "tunnel.stop": _tunnel.Stop(); RequestPush(); break;
            case "tunnel.download": _ = DownloadCloudflaredAsync(); break;
            case "log.export": ExportLog(); break;
            case "log.clear":
                lock (_logs) _logs.Clear();
                SendHistory();
                break;
            case "open.external":
                var url = m.Str("url") ?? "";
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp || u.Scheme == "ms-settings"))
                    _p.OpenExternal(url);
                break;
            case "setup.complete":
                _settings.Update(s => s.SetupComplete = true);
                break;
            case "window.quit": _p.Quit(); break;
        }
    }

    private void SetKey(string target, string key)
    {
        key = key.Trim();
        var protectedKey = _settings.Protect(key);
        if (target.StartsWith("provider:"))
        {
            var id = ProviderPresets.Get(target[9..]).Id;
            _settings.Update(s => s.ProviderConfigFor(id).ApiKeyProtected = protectedKey);
        }
        else if (target == "transcription") _settings.Update(s => s.Transcription.ApiKeyProtected = protectedKey);
        else if (target == "speech") _settings.Update(s => s.Speech.ApiKeyProtected = protectedKey);
        else return;
        Toast("success", key.Length == 0 ? "Key removed." : "Key saved (encrypted for your Windows account).");
    }

    private async Task LoadModelsAsync(string providerId)
    {
        try
        {
            var o = ProviderPresets.Resolve(_settings.Current, _settings, providerId);
            var preset = ProviderPresets.Get(providerId);
            if (preset.RequiresKey && o.ApiKey.Length == 0) throw new ProviderException("Add the API key first.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await ProviderPresets.Create(o).ListModelsAsync(timeout.Token).ConfigureAwait(false);
            Post("models", new { providerId, models, error = (string?)null });
        }
        catch (Exception e)
        {
            Post("models", new { providerId, models = Array.Empty<string>(), error = e is ProviderException pe ? pe.Friendly : e.Message });
        }
    }

    private async Task TestProviderAsync(string providerId)
    {
        var started = DateTimeOffset.Now;
        try
        {
            var o = ProviderPresets.Resolve(_settings.Current, _settings, providerId);
            var reason = ProviderPresets.NotReadyReason(o, ProviderPresets.Get(providerId));
            if (reason is not null) throw new ProviderException(reason);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = await ProviderPresets.Create(o).CompleteAsync(new ChatRequest
            {
                SystemPrompt = "You are a connection test. Reply with exactly: OK",
                Messages = [ChatMessage.UserText("Say OK.")],
                MaxOutputTokens = 256,
            }, timeout.Token).ConfigureAwait(false);
            var ms = (int)(DateTimeOffset.Now - started).TotalMilliseconds;
            Post("providerTest", new { providerId, ok = true, message = $"Connected to {o.Model} in {ms} ms.", reply = TextUtil.Clip(result.Text, 80) });
        }
        catch (Exception e)
        {
            Post("providerTest", new { providerId, ok = false, message = e is ProviderException pe ? pe.Friendly : e.Message, reply = "" });
        }
    }

    private void CopySecret(string which)
    {
        if (!_controlActive) { Toast("info", "Start desktop control to create a connection address."); return; }
        var text = ClientConfig(which);
        if (string.IsNullOrEmpty(text)) { Toast("info", "The public address isn't ready yet."); return; }
        _p.SetClipboard(text);
        Toast("success", which.Contains("public") || which == "public" ? "Public address copied. Keep it private." : "Copied. Keep it private — it controls this PC.");
    }

    private void ExportLog()
    {
        var path = _p.SaveTextFile($"pocket-agent-activity-{DateTime.Now:yyyyMMdd-HHmm}.txt", ActivityText());
        if (path is not null) Toast("success", "Activity saved to " + path);
    }

    private void OnOverlayCommand(string command)
    {
        try
        {
            if (command.StartsWith("answer:"))
            {
                var parts = command.Split(':', 3);
                if (parts.Length == 3) AnswerPrompt(parts[1], parts[2]);
                return;
            }
            switch (command)
            {
                case "talk": ToggleListening("bubble"); break;
                case "pause": PauseAutomation("Paused. Press Resume to continue."); break;
                case "resume": ResumeAutomation(); break;
                case "stop": StopTask(); break;
                case "open": _p.ShowMainWindow(); break;
                case "control": _ = _controlActive ? StopControlAsync() : StartControlAsync(); break;
                case "hush": CancelSpeech(); RequestPush(); break;
            }
        }
        catch (Exception e) { Log("error", "Bubble action failed: " + e.Message); }
    }

    // ---------------------------------------------------------------- views

    public OverlayView BuildOverlayView()
    {
        var s = _settings.Current;
        var talk = TalkKeys.Label(s.TalkKey);
        var hint = s.TalkMode == "hold" ? $"Hold {talk} to talk" : $"Press {talk} to talk";
        var running = _agent.IsRunning;
        var baseView = new OverlayView
        {
            TaskRunning = running,
            Paused = _automationPaused,
            ControlActive = _controlActive,
            TalkHint = hint,
            Visibility = s.BubbleMode,
        };
        var prompt = PromptViews().FirstOrDefault();
        if (prompt is not null) return baseView with { Mode = "prompt", Title = prompt.Title, Caption = prompt.Detail, Prompt = prompt };
        if (_listening) return baseView with { Mode = "listening", Title = "Listening…", Caption = _partial ?? (s.TalkMode == "hold" && !_listenTest ? $"Release {talk} when you're done" : "Pause when you're done") };
        if (_voiceState == "transcribing") return baseView with { Mode = "transcribing", Title = "Understanding…" };
        if (_automationPaused) return baseView with { Mode = "paused", Title = "Paused", Caption = _pauseReason };
        if (_voiceState == "speaking") return baseView with { Mode = "speaking", Title = "Speaking", Caption = _speakingText };
        if (_notice is not null) return baseView with { Mode = "notice", Title = _notice };
        if (running)
        {
            return _agent.State == AgentState.Acting
                ? baseView with { Mode = "acting", Title = _agentDetail, Caption = _lastNarration }
                : baseView with { Mode = "thinking", Title = "Thinking…", Caption = _lastNarration ?? _lastHeard };
        }
        return baseView with { Mode = "idle", Title = _controlActive ? "Ready" : "Pocket Agent", Caption = hint };
    }

    private JsonObject BuildState()
    {
        var s = _settings.Current;
        var options = ProviderPresets.Resolve(s, _settings);
        var preset = ProviderPresets.Get(options.Id);
        var (cost, model) = CostEstimate();
        var usage = _agent.TotalUsage;
        DateTimeOffset? expires;
        lock (_sync) expires = _expiresAt;
        var publicUrl = PublicUrl;

        var state = new JsonObject
        {
            ["version"] = _p.AppVersion,
            ["setupComplete"] = s.SetupComplete,
            ["control"] = new JsonObject
            {
                ["active"] = _controlActive,
                ["starting"] = _starting,
                ["expiresAt"] = expires?.ToUnixTimeMilliseconds(),
                ["autoStopMinutes"] = s.AutoStopMinutes,
                ["port"] = s.Port,
                ["mcpEnabled"] = s.McpServerEnabled,
                ["mcpRunning"] = _server is not null,
                ["mcpError"] = _mcpError,
                ["localUrlMasked"] = _controlActive ? $"http://127.0.0.1:{_server?.Port ?? s.Port}/k/{Mask(_secret)}/mcp" : null,
                ["publicUrlMasked"] = publicUrl is null ? null : publicUrl.Replace(_secret, Mask(_secret)),
            },
            ["tunnel"] = new JsonObject
            {
                ["mode"] = s.TunnelMode,
                ["status"] = _tunnel.Status.ToString().ToLowerInvariant(),
                ["error"] = _tunnel.Error,
                ["progress"] = Math.Round(_tunnel.DownloadProgress, 3),
                ["cloudflaredFound"] = TunnelManager.Find(s.CloudflaredPath, Path.Combine(_p.DataFolder, "bin")) is not null,
                ["customUrl"] = s.CustomTunnelUrl,
            },
            ["agent"] = new JsonObject
            {
                ["state"] = _agent.State.ToString().ToLowerInvariant(),
                ["running"] = _agent.IsRunning,
                ["paused"] = _automationPaused || _agent.IsPaused,
                ["detail"] = _automationPaused ? _pauseReason : _agentDetail,
                ["provider"] = preset.Name,
                ["model"] = model,
                ["notReady"] = AssistantNotReadyReason(),
                ["usage"] = new JsonObject
                {
                    ["input"] = usage.InputTokens,
                    ["output"] = usage.OutputTokens,
                    ["cacheRead"] = usage.CacheReadTokens,
                    ["cacheWrite"] = usage.CacheWriteTokens,
                },
                ["cost"] = cost is double c ? Math.Round(c, 4) : null,
                ["hasHistory"] = _agent.HistoryCount > 0,
            },
            ["voice"] = new JsonObject
            {
                ["state"] = _listening ? "listening" : _voiceState,
                ["talkKey"] = TalkKeys.Label(s.TalkKey),
                ["talkMode"] = s.TalkMode,
                ["partial"] = _partial,
                ["lastHeard"] = _lastHeard,
                ["micTest"] = _micTest,
            },
            ["prompts"] = JsonSerializer.SerializeToNode(PromptViews(), Json.Options),
            ["approvals"] = new JsonObject
            {
                ["session"] = JsonSerializer.SerializeToNode(_approvals.SessionEntries, Json.Options),
                ["always"] = JsonSerializer.SerializeToNode(_approvals.AlwaysEntries, Json.Options),
                ["blocked"] = JsonSerializer.SerializeToNode(_approvals.BlockedEntries, Json.Options),
            },
            ["settings"] = SanitizedSettings(s),
            ["providers"] = JsonSerializer.SerializeToNode(ProviderPresets.All.Select(p => new
            {
                p.Id, p.Name, Style = ProviderPresets.StyleId(p.Style), p.BaseUrl, p.DefaultModel, p.RequiresKey, p.KeyUrl,
                p.SuggestedModels, p.Notes, p.AllowStyleChoice,
            }), Json.Options),
            ["talkKeys"] = JsonSerializer.SerializeToNode(TalkKeys.All, Json.Options),
            ["platform"] = JsonSerializer.SerializeToNode(_platformInfo, Json.Options),
            ["defaultRiskyPhrases"] = RiskPolicy.DefaultSetting,
        };
        return state;
    }

    private static string Mask(string secret) => secret.Length < 8 ? "••••" : secret[..4] + "••••••••" + secret[^4..];

    private JsonObject SanitizedSettings(AppSettings s)
    {
        var node = JsonSerializer.SerializeToNode(s, Json.Options)!.AsObject();
        node.Remove("secretProtected");
        var providers = new JsonObject();
        foreach (var preset in ProviderPresets.All)
        {
            s.Providers.TryGetValue(preset.Id, out var c);
            c ??= new ProviderConfig();
            var key = _settings.Reveal(c.ApiKeyProtected);
            providers[preset.Id] = new JsonObject
            {
                ["baseUrl"] = c.BaseUrl,
                ["model"] = c.Model,
                ["apiStyle"] = c.ApiStyle,
                ["priceInput"] = c.PriceInput,
                ["priceOutput"] = c.PriceOutput,
                ["keySet"] = key.Length > 0,
                ["keyHint"] = SettingsStore.KeyHint(key),
            };
        }
        node["providers"] = providers;
        foreach (var name in new[] { "transcription", "speech" })
        {
            if (node[name] is JsonObject svc)
            {
                var cfg = name == "transcription" ? s.Transcription : s.Speech;
                var key = _settings.Reveal(cfg.ApiKeyProtected);
                svc.Remove("apiKeyProtected");
                svc["keySet"] = key.Length > 0;
                svc["keyHint"] = SettingsStore.KeyHint(key);
            }
        }
        return node;
    }
}
