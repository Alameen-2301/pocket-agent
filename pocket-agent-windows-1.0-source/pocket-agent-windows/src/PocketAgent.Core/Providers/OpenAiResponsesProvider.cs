using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.Providers;

/// <summary>
/// OpenAI's Responses API, which the newest OpenAI models use for tool calling. Runs stateless
/// (store=false): the full, pruned conversation is sent each time so old screenshots can be dropped.
/// </summary>
public sealed class OpenAiResponsesProvider : IChatProvider
{
    private readonly ProviderOptions _o;
    public OpenAiResponsesProvider(ProviderOptions options) { _o = options; }
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
        var node = await HttpJson.SendAsync(() => Make(HttpMethod.Post, "responses", body), ct).ConfigureAwait(false);
        return Parse(node);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        => new OpenAiChatProvider(_o).ListModelsAsync(ct);

    internal JsonObject BuildBody(ChatRequest request)
    {
        var input = new JsonArray();
        foreach (var msg in request.Messages)
        {
            if (msg.Role == ChatRole.Assistant)
            {
                var text = msg.Text;
                if (text.Length > 0) input.Add(new JsonObject { ["role"] = "assistant", ["content"] = text });
                foreach (var c in msg.ToolCalls)
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = c.Id,
                        ["name"] = c.Name,
                        ["arguments"] = c.Arguments.ToJsonString(),
                    });
                continue;
            }

            var images = new List<ImagePart>();
            foreach (var r in msg.ToolResults)
            {
                var resultImages = r.Content.OfType<ImagePart>().ToList();
                var text = r.Text;
                if (resultImages.Count > 0) text += (text.Length > 0 ? "\n" : "") + $"[{resultImages.Count} image(s) from this tool are attached in the next message]";
                if (r.IsError && !text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) text = "Error: " + text;
                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = r.ToolCallId, ["output"] = text.Length > 0 ? text : "Done." });
                images.AddRange(resultImages);
            }
            var content = new JsonArray();
            if (images.Count > 0)
            {
                content.Add(new JsonObject { ["type"] = "input_text", ["text"] = "Images returned by the tool calls above:" });
                foreach (var i in images) content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = i.DataUrl });
            }
            foreach (var p in msg.Parts)
            {
                if (p is TextPart { Text.Length: > 0 } t) content.Add(new JsonObject { ["type"] = "input_text", ["text"] = t.Text });
                else if (p is ImagePart i) content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = i.DataUrl });
            }
            if (content.Count > 0) input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        }

        var body = new JsonObject
        {
            ["model"] = _o.Model,
            ["instructions"] = request.SystemPrompt,
            ["input"] = input,
            ["store"] = false,
        };
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                var tool = new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema.DeepClone(),
                    // Optional arguments need non-strict schemas.
                    ["strict"] = false,
                };
                tools.Add(tool);
            }
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }
        return body;
    }

    internal static ChatResult Parse(JsonNode node)
    {
        var usage = node["usage"];
        long cached = usage?["input_tokens_details"].Long("cached_tokens") ?? 0;
        var status = node.Str("status") ?? "";
        var result = new ChatResult
        {
            StopReason = status == "incomplete" ? node["incomplete_details"].Str("reason") ?? "incomplete" : status,
            Usage = new TokenUsage(Math.Max(0, usage.Long("input_tokens") - cached), usage.Long("output_tokens"), cached, 0),
        };
        if (status == "failed" && node["error"] is not null)
            throw new ProviderException("AI provider error: " + (node["error"].Str("message") ?? "the response failed"));

        foreach (var item in node["output"] as JsonArray ?? [])
        {
            switch (item.Str("type"))
            {
                case "message":
                    foreach (var c in item?["content"] as JsonArray ?? [])
                    {
                        var type = c.Str("type");
                        var text = type == "refusal" ? c.Str("refusal") : c.Str("text");
                        if (!string.IsNullOrEmpty(text)) result.Parts.Add(new TextPart(text));
                    }
                    break;
                case "function_call":
                    var args = Json.ParseArguments(item.Str("arguments"), out var error);
                    var callId = item.Str("call_id") ?? item.Str("id") ?? Ids.New("call");
                    result.Parts.Add(new ToolCallPart(callId, item.Str("name") ?? "", args, error));
                    break;
            }
        }
        return result;
    }
}
