using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.Providers;

/// <summary>
/// The OpenAI Chat Completions format, spoken by Gemini (OpenAI compatibility endpoint), OpenRouter,
/// Groq, Mistral, xAI, Ollama, LM Studio and most other providers.
/// </summary>
public sealed class OpenAiChatProvider : IChatProvider
{
    private readonly ProviderOptions _o;
    public OpenAiChatProvider(ProviderOptions options) { _o = options; }
    public string DisplayName => _o.DisplayName;
    public string Model => _o.Model;

    private HttpRequestMessage Make(HttpMethod method, string path, JsonNode? body)
    {
        var m = new HttpRequestMessage(method, HttpJson.JoinUrl(_o.BaseUrl, path));
        if (!string.IsNullOrWhiteSpace(_o.ApiKey)) m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);
        foreach (var (k, v) in _o.ExtraHeaders) m.Headers.TryAddWithoutValidation(k, v);
        if (body is not null) m.Content = HttpJson.JsonContent(body);
        return m;
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var body = BuildBody(request);
        var node = await HttpJson.SendAsync(() => Make(HttpMethod.Post, "chat/completions", body), ct).ConfigureAwait(false);
        return Parse(node);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var node = await HttpJson.SendAsync(() => Make(HttpMethod.Get, "models", null), ct, 2).ConfigureAwait(false);
        var list = node["data"] as JsonArray ?? node["models"] as JsonArray ?? [];
        return list.Select(m => m.Str("id") ?? m.Str("name"))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!.StartsWith("models/") ? id[7..] : id!)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    internal JsonObject BuildBody(ChatRequest request)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt } };
        foreach (var msg in request.Messages)
        {
            if (msg.Role == ChatRole.Assistant)
            {
                var a = new JsonObject { ["role"] = "assistant" };
                var text = msg.Text;
                var calls = msg.ToolCalls.ToList();
                if (calls.Count > 0)
                {
                    a["content"] = text.Length > 0 ? text : null;
                    var arr = new JsonArray();
                    foreach (var c in calls)
                        arr.Add(new JsonObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.ToJsonString() },
                        });
                    a["tool_calls"] = arr;
                }
                else a["content"] = text;
                messages.Add(a);
                continue;
            }

            // User turn: tool results become "tool" messages (text only), immediately after the assistant call.
            var pendingImages = new List<ImagePart>();
            foreach (var r in msg.ToolResults)
            {
                var images = r.Content.OfType<ImagePart>().ToList();
                var text = r.Text;
                if (images.Count > 0) text += (text.Length > 0 ? "\n" : "") + $"[{images.Count} image(s) from this tool are attached in the next message]";
                if (r.IsError && !text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) text = "Error: " + text;
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.ToolCallId, ["content"] = text.Length > 0 ? text : "Done." });
                pendingImages.AddRange(images);
            }
            var parts = new JsonArray();
            if (pendingImages.Count > 0)
            {
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = "Images returned by the tool calls above:" });
                foreach (var i in pendingImages) parts.Add(ImageUrl(i));
            }
            foreach (var p in msg.Parts)
            {
                if (p is TextPart { Text.Length: > 0 } t) parts.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                else if (p is ImagePart i) parts.Add(ImageUrl(i));
            }
            if (parts.Count == 0) continue;
            bool onlyText = parts.All(p => p.Str("type") == "text");
            JsonNode content = onlyText
                ? JsonValue.Create(string.Join("\n\n", parts.Select(p => p.Str("text"))))
                : parts;
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        }

        var body = new JsonObject { ["model"] = _o.Model, ["messages"] = messages };
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                var fn = new JsonObject { ["name"] = t.Name, ["description"] = t.Description };
                if (HttpJson.CompactSchema(t) is JsonObject schema) fn["parameters"] = schema;
                tools.Add(new JsonObject { ["type"] = "function", ["function"] = fn });
            }
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }
        return body;
    }

    private static JsonObject ImageUrl(ImagePart i) => new()
    {
        ["type"] = "image_url",
        ["image_url"] = new JsonObject { ["url"] = i.DataUrl },
    };

    internal static ChatResult Parse(JsonNode node)
    {
        var choice = (node["choices"] as JsonArray)?.FirstOrDefault();
        var message = choice?["message"];
        var usage = node["usage"];
        long cached = usage?["prompt_tokens_details"].Long("cached_tokens") ?? 0;
        long prompt = usage.Long("prompt_tokens");
        var result = new ChatResult
        {
            StopReason = choice.Str("finish_reason") ?? "",
            Usage = new TokenUsage(Math.Max(0, prompt - cached), usage.Long("completion_tokens"), cached, 0),
        };
        if (message is null)
        {
            if (node["error"] is not null) throw new ProviderException("AI provider error: " + (HttpJson.ExtractError(node.ToJsonString()) ?? "unknown"));
            return result;
        }
        switch (message["content"])
        {
            case JsonValue v when v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s):
                result.Parts.Add(new TextPart(s));
                break;
            case JsonArray arr:
                foreach (var part in arr)
                    if (part.Str("text") is { Length: > 0 } pt) result.Parts.Add(new TextPart(pt));
                break;
        }
        foreach (var call in message["tool_calls"] as JsonArray ?? [])
        {
            var fn = call?["function"];
            var name = fn.Str("name") ?? "";
            JsonObject args;
            string? error = null;
            if (fn?["arguments"] is JsonObject obj) args = (JsonObject)obj.DeepClone();
            else args = Json.ParseArguments(fn.Str("arguments"), out error);
            var id = call.Str("id");
            result.Parts.Add(new ToolCallPart(string.IsNullOrEmpty(id) ? Ids.New("call") : id, name, args, error));
        }
        return result;
    }
}
