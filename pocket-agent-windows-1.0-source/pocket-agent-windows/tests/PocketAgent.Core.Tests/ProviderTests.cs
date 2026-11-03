using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Providers;
using PocketAgent.Core.Tools;
using PocketAgent.Core.Voice;

namespace PocketAgent.Core.Tests;

public static class ProviderTests
{
    private static ProviderOptions Options(ApiStyle style, string baseUrl, string key = "test-key") => new()
    {
        Id = "t",
        DisplayName = "Test",
        Style = style,
        BaseUrl = baseUrl,
        ApiKey = key,
        Model = "model-x",
    };

    /// <summary>A conversation with a screenshot tool result, which exercises images in tool results.</summary>
    private static List<ChatMessage> Conversation() =>
    [
        ChatMessage.UserText("Open Notepad"),
        new(ChatRole.Assistant, [new TextPart("Taking a look."), new ToolCallPart("call_1", "windows_screenshot", new JsonObject { ["scope"] = "window" })]),
        new(ChatRole.User, [new ToolResultPart("call_1", "windows_screenshot", [new TextPart("Screenshot 100x100"), new ImagePart("image/jpeg", "QUJD")], false), new TextPart("also save it")]),
    ];

    private static Task Json(HttpContext ctx, string json, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(json);
    }

    [Test]
    public static async Task AnthropicRequestShapeAndToolUseParsing()
    {
        await using var server = await FakeHttpServer.StartAsync((ctx, body, i) => Json(ctx, """
            {"id":"msg_1","type":"message","role":"assistant","stop_reason":"tool_use",
             "content":[{"type":"text","text":"Clicking Save."},{"type":"tool_use","id":"toolu_9","name":"windows_tap","input":{"element":4}}],
             "usage":{"input_tokens":120,"output_tokens":30,"cache_read_input_tokens":1000,"cache_creation_input_tokens":50}}
            """));
        var p = new AnthropicProvider(Options(ApiStyle.Anthropic, server.BaseUrl));
        var result = await p.CompleteAsync(new ChatRequest { SystemPrompt = "sys", Messages = Conversation(), Tools = ToolCatalog.All }, CancellationToken.None);

        var req = server.Requests.Single();
        Assert.Equal("/v1/messages", req.Path);
        Assert.Equal("test-key", req.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", req.Headers["anthropic-version"]);
        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal("model-x", body["model"]!.GetValue<string>());
        Assert.Equal("ephemeral", body["system"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("ephemeral", body["tools"]!.AsArray()[^1]!["cache_control"]!["type"]!.GetValue<string>(), "last tool cached");
        var userTurn = body["messages"]![2]!["content"]!.AsArray();
        Assert.Equal("tool_result", userTurn[0]!["type"]!.GetValue<string>(), "tool result first");
        Assert.Equal("image", userTurn[0]!["content"]![1]!["type"]!.GetValue<string>(), "image inside tool result");
        Assert.Equal("text", userTurn[1]!["type"]!.GetValue<string>());
        Assert.Equal("ephemeral", userTurn[^1]!["cache_control"]!["type"]!.GetValue<string>(), "conversation prefix cached");
        Assert.Equal("tool_use", body["messages"]![1]!["content"]![1]!["type"]!.GetValue<string>());

        Assert.Equal("Clicking Save.", result.Text);
        var call = result.ToolCalls.Single();
        Assert.Equal("toolu_9", call.Id);
        Assert.Equal(4, call.Arguments["element"]!.GetValue<int>());
        Assert.Equal(1000L, result.Usage.CacheReadTokens);
        Assert.Equal(50L, result.Usage.CacheWriteTokens);
    }

    [Test]
    public static async Task OpenAiChatMovesToolImagesIntoAUserMessage()
    {
        await using var server = await FakeHttpServer.StartAsync((ctx, body, i) => Json(ctx, """
            {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
              "tool_calls":[{"id":"call_7","type":"function","function":{"name":"windows_key","arguments":"{\"keys\":\"ctrl+s\"}"}}]}}],
             "usage":{"prompt_tokens":500,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":300}}}
            """));
        var p = new OpenAiChatProvider(Options(ApiStyle.OpenAiChat, server.BaseUrl + "/v1"));
        var result = await p.CompleteAsync(new ChatRequest { SystemPrompt = "sys", Messages = Conversation(), Tools = ToolCatalog.All }, CancellationToken.None);
        var req = server.Requests.Single();
        Assert.Equal("/v1/chat/completions", req.Path);
        Assert.Equal("Bearer test-key", req.Headers["authorization"]);
        var messages = JsonNode.Parse(req.Body)!["messages"]!.AsArray();
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("assistant", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[3]!["role"]!.GetValue<string>(), "tool message right after the call");
        Assert.Contains("attached in the next message", messages[3]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[4]!["role"]!.GetValue<string>());
        Assert.Equal("image_url", messages[4]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Contains("data:image/jpeg;base64,QUJD", messages[4]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
        var statusTool = JsonNode.Parse(req.Body)!["tools"]!.AsArray().First(t => t!["function"]!["name"]!.GetValue<string>() == "windows_status")!;
        Assert.True(statusTool["function"]!["parameters"] is null, "empty schema omitted for compatibility");

        var call = result.ToolCalls.Single();
        Assert.Equal("windows_key", call.Name);
        Assert.Equal("ctrl+s", call.Arguments["keys"]!.GetValue<string>());
        Assert.Equal(200L, result.Usage.InputTokens, "uncached input");
        Assert.Equal(300L, result.Usage.CacheReadTokens);
    }

    [Test]
    public static async Task OpenAiChatReportsInvalidArgumentsInsteadOfCrashing()
    {
        await using var server = await FakeHttpServer.StartAsync((ctx, body, i) => Json(ctx, """
            {"choices":[{"message":{"role":"assistant","tool_calls":[{"function":{"name":"windows_tap","arguments":"{not json"}}]}}]}
            """));
        var result = await new OpenAiChatProvider(Options(ApiStyle.OpenAiChat, server.BaseUrl)).CompleteAsync(
            new ChatRequest { SystemPrompt = "s", Messages = [ChatMessage.UserText("hi")] }, CancellationToken.None);
        var call = result.ToolCalls.Single();
        Assert.True(call.ParseError is not null, "parse error captured");
        Assert.True(call.Id.StartsWith("call_"), "id generated");
    }

    [Test]
    public static async Task OpenAiResponsesShape()
    {
        await using var server = await FakeHttpServer.StartAsync((ctx, body, i) => Json(ctx, """
            {"id":"resp_1","status":"completed","output":[
               {"type":"reasoning","id":"rs_1","summary":[]},
               {"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Saved it."}]},
               {"type":"function_call","id":"fc_1","call_id":"call_42","name":"windows_wait","arguments":"{\"seconds\":1}"}],
             "usage":{"input_tokens":900,"output_tokens":12,"input_tokens_details":{"cached_tokens":600}}}
            """));
        var p = new OpenAiResponsesProvider(Options(ApiStyle.OpenAiResponses, server.BaseUrl + "/v1"));
        var result = await p.CompleteAsync(new ChatRequest { SystemPrompt = "sys", Messages = Conversation(), Tools = ToolCatalog.All }, CancellationToken.None);
        var req = server.Requests.Single();
        Assert.Equal("/v1/responses", req.Path);
        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal("sys", body["instructions"]!.GetValue<string>());
        Assert.Equal(false, body["store"]!.GetValue<bool>());
        Assert.Equal(false, body["tools"]![0]!["strict"]!.GetValue<bool>(), "non-strict for optional args");
        var input = body["input"]!.AsArray();
        Assert.Equal("function_call", input[2]!["type"]!.GetValue<string>());
        Assert.True(input[2]!["id"] is null, "no item id, so no reasoning item is required");
        Assert.Equal("function_call_output", input[3]!["type"]!.GetValue<string>());
        Assert.Equal("input_image", input[4]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("Saved it.", result.Text);
        Assert.Equal("call_42", result.ToolCalls.Single().Id);
        Assert.Equal(300L, result.Usage.InputTokens);
    }

    [Test]
    public static async Task RetriesOnRateLimitThenSucceeds()
    {
        HttpJson.BackoffForAttempt = _ => TimeSpan.FromMilliseconds(10);
        await using var server = await FakeHttpServer.StartAsync((ctx, body, i) =>
        {
            if (i == 0)
            {
                ctx.Response.Headers["Retry-After"] = "0";
                return Json(ctx, "{\"error\":{\"message\":\"slow down\"}}", 429);
            }
            return Json(ctx, "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"usage\":{}}");
        });
        var r = await new AnthropicProvider(Options(ApiStyle.Anthropic, server.BaseUrl)).CompleteAsync(
            new ChatRequest { SystemPrompt = "s", Messages = [ChatMessage.UserText("x")] }, CancellationToken.None);
        Assert.Equal("ok", r.Text);
        Assert.Equal(2, server.Requests.Count);
    }

    [Test]
    public static async Task AuthAndContextErrorsAreClassified()
    {
        await using var server = await FakeHttpServer.StartAsync((ctx, body, i) => i == 0
            ? Json(ctx, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}", 401)
            : Json(ctx, "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"prompt is too long: 250000 tokens > 200000 maximum\"}}", 400));
        var p = new AnthropicProvider(Options(ApiStyle.Anthropic, server.BaseUrl));
        var req = new ChatRequest { SystemPrompt = "s", Messages = [ChatMessage.UserText("x")] };
        ProviderException? auth = null, ctxLen = null;
        try { await p.CompleteAsync(req, CancellationToken.None); } catch (ProviderException e) { auth = e; }
        try { await p.CompleteAsync(req, CancellationToken.None); } catch (ProviderException e) { ctxLen = e; }
        Assert.True(auth is { IsAuth: true }, "auth");
        Assert.Contains("API key", auth!.Friendly);
        Assert.True(ctxLen is { IsContextTooLong: true }, "context length");
    }

    [Test]
    public static async Task ModelListsAreParsed()
    {
        await using var server = await FakeHttpServer.StartAsync((ctx, body, i) => Json(ctx,
            ctx.Request.Path.Value!.Contains("/v1/models") && ctx.Request.Headers.ContainsKey("x-api-key")
                ? "{\"data\":[{\"id\":\"claude-sonnet-5-5\"},{\"id\":\"claude-opus-5-5\"}]}"
                : "{\"data\":[{\"id\":\"models/gemini-3.8-flash\"},{\"id\":\"b\"},{\"id\":\"b\"}]}"));
        var a = await new AnthropicProvider(Options(ApiStyle.Anthropic, server.BaseUrl)).ListModelsAsync(CancellationToken.None);
        Assert.Equal(2, a.Count);
        var o = await new OpenAiChatProvider(Options(ApiStyle.OpenAiChat, server.BaseUrl)).ListModelsAsync(CancellationToken.None);
        Assert.Equal("b,gemini-3.8-flash", string.Join(",", o));
    }

    [Test]
    public static async Task WhisperAndSpeechEndpoints()
    {
        var wavReply = Wav.FromPcm16(new byte[480], 24000);
        await using var server = await FakeHttpServer.StartAsync(async (ctx, body, i) =>
        {
            if (ctx.Request.Path.Value!.EndsWith("/audio/transcriptions")) await Json(ctx, "{\"text\":\" Open Notepad. \"}");
            else { ctx.Response.ContentType = "audio/wav"; await ctx.Response.Body.WriteAsync(wavReply); }
        });
        var text = await new WhisperApiTranscriber(server.BaseUrl + "/v1", "k", "gpt-transcribe").TranscribeAsync(Wav.FromPcm16(new byte[3200], 16000), "de-DE", CancellationToken.None);
        Assert.Equal("Open Notepad.", text);
        var form = server.Requests[0].Body;
        Assert.Contains("name=model", form);
        Assert.Contains("gpt-transcribe", form);
        Assert.Contains("name=language", form);
        var audio = await new OpenAiSpeechSynthesizer(server.BaseUrl + "/v1", "k", "", "nova", 1.0).SynthesizeAsync("Hello", "auto", CancellationToken.None);
        Assert.True(Wav.TryRead(audio, out var info, out _), "wav");
        Assert.Equal(24000, info.SampleRate);
        var speechBody = JsonNode.Parse(server.Requests[1].Body)!;
        Assert.Equal("nova", speechBody["voice"]!.GetValue<string>());
        Assert.Equal("gpt-4o-mini-tts", speechBody["model"]!.GetValue<string>());
        Assert.Equal(null, WhisperApiTranscriber.IsoLanguage("auto"));
    }

    [Test]
    public static void PresetsResolveAndExplainMissingSetup()
    {
        var dir = TempDir.New();
        var store = new Settings.SettingsStore(dir, new FakeProtector());
        var o = ProviderPresets.Resolve(store.Current, store, "anthropic");
        Assert.Equal("claude-sonnet-5-5", o.Model);
        Assert.Contains("API key", ProviderPresets.NotReadyReason(o, ProviderPresets.Get("anthropic"))!);
        store.Update(s => { var c = s.ProviderConfigFor("openai"); c.ApiKeyProtected = store.Protect("sk"); c.ApiStyle = "openai-chat"; });
        var oa = ProviderPresets.Resolve(store.Current, store, "openai");
        Assert.Equal(ApiStyle.OpenAiChat, oa.Style, "style override for OpenAI");
        Assert.Equal(null, ProviderPresets.NotReadyReason(oa, ProviderPresets.Get("openai")));
        var ollama = ProviderPresets.Resolve(store.Current, store, "ollama");
        Assert.Contains("model", ProviderPresets.NotReadyReason(ollama, ProviderPresets.Get("ollama"))!, "needs a model, not a key");
        var cost = ProviderPresets.EstimateCost(new TokenUsage(1_000_000, 100_000, 1_000_000, 0), ProviderPresets.PriceFor("claude-sonnet-5-5", null));
        Assert.Equal(3.1, Math.Round(cost!.Value, 3), "2 + 1 + 0.1");
    }
}
