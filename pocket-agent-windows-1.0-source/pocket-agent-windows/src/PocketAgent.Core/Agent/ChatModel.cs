using System.Text.Json.Nodes;

namespace PocketAgent.Core.Agent;

/// <summary>Who wrote a message in the provider-neutral conversation.</summary>
public enum ChatRole { User, Assistant }

/// <summary>One piece of a message: text, an image, a tool call or a tool result.</summary>
public abstract class ContentPart { }

public sealed class TextPart : ContentPart
{
    public TextPart(string text) { Text = text ?? ""; }
    public string Text { get; }
}

public sealed class ImagePart : ContentPart
{
    public ImagePart(string mediaType, string base64)
    {
        MediaType = mediaType;
        Base64 = base64;
    }
    public string MediaType { get; }
    public string Base64 { get; }
    public string DataUrl => $"data:{MediaType};base64,{Base64}";
}

public sealed class ToolCallPart : ContentPart
{
    public ToolCallPart(string id, string name, JsonObject arguments, string? parseError = null)
    {
        Id = id;
        Name = name;
        Arguments = arguments;
        ParseError = parseError;
    }
    public string Id { get; }
    public string Name { get; }
    public JsonObject Arguments { get; }
    /// <summary>Set when the model produced arguments that were not valid JSON.</summary>
    public string? ParseError { get; }
}

public sealed class ToolResultPart : ContentPart
{
    public ToolResultPart(string toolCallId, string toolName, List<ContentPart> content, bool isError)
    {
        ToolCallId = toolCallId;
        ToolName = toolName;
        Content = content;
        IsError = isError;
    }
    public string ToolCallId { get; }
    public string ToolName { get; }
    /// <summary>Only <see cref="TextPart"/> and <see cref="ImagePart"/> entries.</summary>
    public List<ContentPart> Content { get; }
    public bool IsError { get; }
    public string Text => string.Join("\n", Content.OfType<TextPart>().Select(t => t.Text));
}

public sealed class ChatMessage
{
    public ChatMessage(ChatRole role, IEnumerable<ContentPart> parts)
    {
        Role = role;
        Parts = parts.ToList();
    }
    public ChatRole Role { get; }
    public List<ContentPart> Parts { get; }

    public static ChatMessage UserText(string text) => new(ChatRole.User, [new TextPart(text)]);
    public static ChatMessage AssistantText(string text) => new(ChatRole.Assistant, [new TextPart(text)]);

    public IEnumerable<ToolCallPart> ToolCalls => Parts.OfType<ToolCallPart>();
    public IEnumerable<ToolResultPart> ToolResults => Parts.OfType<ToolResultPart>();
    public string Text => string.Join("\n", Parts.OfType<TextPart>().Select(t => t.Text).Where(t => t.Length > 0)).Trim();
}

/// <summary>Token counts for one model call or a whole session.</summary>
public sealed record TokenUsage(long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0)
{
    public static readonly TokenUsage Zero = new(0, 0, 0, 0);
    public TokenUsage Add(TokenUsage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CacheReadTokens + other.CacheReadTokens,
        CacheWriteTokens + other.CacheWriteTokens);
    public long TotalInput => InputTokens + CacheReadTokens + CacheWriteTokens;
}
