using System.Text.Json.Nodes;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.Providers;

/// <summary>Claude via the Anthropic Messages API, with prompt caching for long agent runs.</summary>
public sealed class AnthropicProvider : IChatProvider
{
    private readonly ProviderOptions _o;
    public AnthropicProvider(ProviderOptions options) { _o = options; }
    public string DisplayName => _o.DisplayName;
    public string Model => _o.Model;

    private string Url(string path)
    {
        var b = _o.BaseUrl.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b + "/" + path : b + "/v1/" + path;
    }

    private HttpRequestMessage Make(HttpMethod method, string path, JsonNode? body)
    {
        var m = new HttpRequestMessage(method, Url(path));
        m.Headers.TryAddWithoutValidation("x-api-key", _o.ApiKey);
        m.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        foreach (var (k, v) in _o.ExtraHeaders) m.Headers.TryAddWithoutValidation(k, v);
        if (body is not null) m.Content = HttpJson.JsonContent(body);
        return m;
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var body = BuildBody(request);
        var node = await HttpJson.SendAsync(() => Make(HttpMethod.Post, "messages", body), ct).ConfigureAwait(false);
        return Parse(node);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var node = await HttpJson.SendAsync(() => Make(HttpMethod.Get, "models?limit=1000", null), ct, 2).ConfigureAwait(false);
        return (node["data"] as JsonArray ?? [])
            .Select(m => m.Str("id"))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }

    internal JsonObject BuildBody(ChatRequest request)
    {
        var cache = new JsonObject { ["type"] = "ephemeral" };
        var body = new JsonObject
        {
            ["model"] = _o.Model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = request.SystemPrompt,
                ["cache_control"] = cache.DeepClone(),
            }),
        };
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
                tools.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.InputSchema.DeepClone() });
            ((JsonObject)tools[^1]!)["cache_control"] = cache.DeepClone();
            body["tools"] = tools;
        }

        var messages = new JsonArray();
        foreach (var msg in Merge(request.Messages))
        {
            var content = new JsonArray();
            if (msg.Role == ChatRole.User)
            {
                // Tool results must come first in a user turn, then any text or images.
                foreach (var r in msg.Parts.OfType<ToolResultPart>()) content.Add(ToolResult(r));
                foreach (var p in msg.Parts.Where(p => p is not ToolResultPart))
                    if (Block(p) is JsonObject b) content.Add(b);
            }
            else
            {
                foreach (var p in msg.Parts)
                    if (Block(p) is JsonObject b) content.Add(b);
            }
            if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = msg.Role == ChatRole.User ? "(continue)" : "(no reply)" });
            messages.Add(new JsonObject { ["role"] = msg.Role == ChatRole.User ? "user" : "assistant", ["content"] = content });
        }
        // Cache the conversation prefix so each step of a long run only pays for the new tail.
        if (messages.Count > 0 && messages[^1]?["content"] is JsonArray last && last.Count > 0 && last[^1] is JsonObject lastBlock)
            lastBlock["cache_control"] = cache.DeepClone();
        body["messages"] = messages;
        return body;
    }

    /// <summary>The API expects alternating roles; merge any accidental neighbours.</summary>
    private static IEnumerable<ChatMessage> Merge(IReadOnlyList<ChatMessage> messages)
    {
        ChatMessage? pending = null;
        foreach (var m in messages)
        {
            if (pending is not null && pending.Role == m.Role)
            {
                pending = new ChatMessage(m.Role, pending.Parts.Concat(m.Parts));
                continue;
            }
            if (pending is not null) yield return pending;
            pending = m;
        }
        if (pending is not null) yield return pending;
    }

    private static JsonObject? Block(ContentPart p) => p switch
    {
        TextPart { Text.Length: > 0 } t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
        ImagePart i => new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = i.MediaType, ["data"] = i.Base64 },
        },
        ToolCallPart c => new JsonObject
        {
            ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = c.Arguments.DeepClone(),
        },
        _ => null,
    };

    private static JsonObject ToolResult(ToolResultPart r)
    {
        var inner = new JsonArray();
        foreach (var p in r.Content)
            if (Block(p) is JsonObject b) inner.Add(b);
        if (inner.Count == 0) inner.Add(new JsonObject { ["type"] = "text", ["text"] = r.IsError ? "Failed." : "Done." });
        return new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = r.ToolCallId,
            ["content"] = inner,
            ["is_error"] = r.IsError,
        };
    }

    internal static ChatResult Parse(JsonNode node)
    {
        var usage = node["usage"];
        var result = new ChatResult
        {
            StopReason = node.Str("stop_reason") ?? "",
            Usage = new TokenUsage(usage.Long("input_tokens"), usage.Long("output_tokens"),
                usage.Long("cache_read_input_tokens"), usage.Long("cache_creation_input_tokens")),
        };
        foreach (var block in node["content"] as JsonArray ?? [])
        {
            switch (block.Str("type"))
            {
                case "text":
                    var text = block.Str("text") ?? "";
                    if (text.Length > 0) result.Parts.Add(new TextPart(text));
                    break;
                case "tool_use":
                    var input = block?["input"] as JsonObject;
                    result.Parts.Add(new ToolCallPart(block.Str("id") ?? Ids.New("toolu"), block.Str("name") ?? "",
                        input is null ? new JsonObject() : (JsonObject)input.DeepClone()));
                    break;
            }
        }
        return result;
    }
}
