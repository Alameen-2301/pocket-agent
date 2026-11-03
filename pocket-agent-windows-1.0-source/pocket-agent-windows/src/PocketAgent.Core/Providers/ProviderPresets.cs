using PocketAgent.Core.Agent;
using PocketAgent.Core.Settings;

namespace PocketAgent.Core.Providers;

public sealed record ProviderPreset(
    string Id,
    string Name,
    ApiStyle Style,
    string BaseUrl,
    string DefaultModel,
    bool RequiresKey,
    string KeyUrl,
    string[] SuggestedModels,
    string Notes,
    bool AllowStyleChoice = false);

public sealed record ModelPrice(double Input, double Output, double CacheReadMultiplier = 0.1, double CacheWriteMultiplier = 1.25);

/// <summary>
/// Built-in provider choices. Model IDs were taken from the providers' model pages (November 2026);
/// "Load models" in Settings fetches the live list, so nothing here is a hard dependency.
/// </summary>
public static class ProviderPresets
{
    public static readonly ProviderPreset[] All =
    [
        new("anthropic", "Anthropic (Claude)", ApiStyle.Anthropic, "https://api.anthropic.com", "claude-sonnet-5-5", true,
            "https://console.anthropic.com/settings/keys",
            ["claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5", "claude-fable-5-1"],
            "Strong at operating apps from screenshots. Uses prompt caching to keep long tasks cheap."),
        new("openai", "OpenAI", ApiStyle.OpenAiResponses, "https://api.openai.com/v1", "gpt-6.1-terra", true,
            "https://platform.openai.com/api-keys",
            ["gpt-6.1-terra", "gpt-6-luna", "gpt-6.1-sol", "gpt-6-astra"],
            "Uses the Responses API. Switch to Chat Completions if you use an older proxy.", AllowStyleChoice: true),
        new("gemini", "Google Gemini", ApiStyle.OpenAiChat, "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.8-flash", true,
            "https://aistudio.google.com/apikey",
            ["gemini-3.8-flash", "gemini-3.5-flash-lite", "gemini-4-argon"],
            "Uses Google's OpenAI-compatible endpoint."),
        new("openrouter", "OpenRouter", ApiStyle.OpenAiChat, "https://openrouter.ai/api/v1", "", true,
            "https://openrouter.ai/keys", [],
            "One key for hundreds of models. Choose a model that supports tools and images."),
        new("groq", "Groq", ApiStyle.OpenAiChat, "https://api.groq.com/openai/v1", "", true,
            "https://console.groq.com/keys", [], "Very fast. Pick a model with tool calling and vision."),
        new("mistral", "Mistral", ApiStyle.OpenAiChat, "https://api.mistral.ai/v1", "", true,
            "https://console.mistral.ai/api-keys", [], "Pick a model with tool calling and vision."),
        new("xai", "xAI (Grok)", ApiStyle.OpenAiChat, "https://api.x.ai/v1", "", true,
            "https://console.x.ai", [], "Pick a model with tool calling and vision."),
        new("ollama", "Ollama (on this PC)", ApiStyle.OpenAiChat, "http://localhost:11434/v1", "", false,
            "https://ollama.com/search?c=vision", [],
            "Runs locally, nothing leaves your PC. Needs a model with tools and vision."),
        new("lmstudio", "LM Studio (on this PC)", ApiStyle.OpenAiChat, "http://localhost:1234/v1", "", false,
            "https://lmstudio.ai", [], "Runs locally. Load a model with tool use and vision."),
        new("custom", "Custom (OpenAI-compatible)", ApiStyle.OpenAiChat, "", "", false,
            "", [], "Any server that speaks the OpenAI Chat Completions or Anthropic Messages format.", AllowStyleChoice: true),
    ];

    public static ProviderPreset Get(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    private static readonly Dictionary<string, ModelPrice> KnownPrices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5-1"] = new(10, 50, 0.025),
        ["claude-opus-5-5"] = new(4, 20, 0.05),
        ["claude-sonnet-5-5"] = new(2, 10, 0.05),
        ["gpt-6-luna"] = new(0.10, 0.50),
    };

    public static ApiStyle ParseStyle(string? style, ApiStyle fallback) => style?.ToLowerInvariant() switch
    {
        "anthropic" => ApiStyle.Anthropic,
        "openai-chat" or "chat" => ApiStyle.OpenAiChat,
        "openai-responses" or "responses" => ApiStyle.OpenAiResponses,
        _ => fallback,
    };

    public static string StyleId(ApiStyle style) => style switch
    {
        ApiStyle.Anthropic => "anthropic",
        ApiStyle.OpenAiResponses => "openai-responses",
        _ => "openai-chat",
    };

    /// <summary>Resolved settings for one provider: preset defaults overridden by the user's config.</summary>
    public static ProviderOptions Resolve(AppSettings settings, SettingsStore store, string? providerId = null)
    {
        var id = providerId ?? settings.ProviderId;
        var preset = Get(id);
        settings.Providers.TryGetValue(preset.Id, out var config);
        config ??= new ProviderConfig();
        var style = preset.AllowStyleChoice ? ParseStyle(config.ApiStyle, preset.Style) : preset.Style;
        var headers = new Dictionary<string, string>();
        if (preset.Id == "openrouter")
        {
            headers["HTTP-Referer"] = "https://github.com/pocket-agent/pocket-agent-windows";
            headers["X-Title"] = "Pocket Agent";
        }
        return new ProviderOptions
        {
            Id = preset.Id,
            DisplayName = preset.Name,
            Style = style,
            BaseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? preset.BaseUrl : config.BaseUrl.Trim(),
            ApiKey = store.Reveal(config.ApiKeyProtected),
            Model = string.IsNullOrWhiteSpace(config.Model) ? preset.DefaultModel : config.Model.Trim(),
            ExtraHeaders = headers,
        };
    }

    public static IChatProvider Create(ProviderOptions o) => o.Style switch
    {
        ApiStyle.Anthropic => new AnthropicProvider(o),
        ApiStyle.OpenAiResponses => new OpenAiResponsesProvider(o),
        _ => new OpenAiChatProvider(o),
    };

    /// <summary>Why the assistant can't run yet, or null when it can.</summary>
    public static string? NotReadyReason(ProviderOptions o, ProviderPreset preset)
    {
        if (string.IsNullOrWhiteSpace(o.BaseUrl)) return "Enter the server address for your custom provider in Settings → AI model.";
        if (preset.RequiresKey && string.IsNullOrWhiteSpace(o.ApiKey)) return $"Add your {preset.Name} API key in Settings → AI model.";
        if (string.IsNullOrWhiteSpace(o.Model)) return "Choose a model in Settings → AI model (use “Load models”).";
        return null;
    }

    public static ModelPrice? PriceFor(string model, ProviderConfig? config)
    {
        if (config?.PriceInput is double pin && config.PriceOutput is double pout) return new ModelPrice(pin, pout);
        return KnownPrices.TryGetValue(model, out var p) ? p : null;
    }

    public static double? EstimateCost(TokenUsage usage, ModelPrice? price)
    {
        if (price is null) return null;
        double input = usage.InputTokens * price.Input
                       + usage.CacheReadTokens * price.Input * price.CacheReadMultiplier
                       + usage.CacheWriteTokens * price.Input * price.CacheWriteMultiplier;
        return (input + usage.OutputTokens * price.Output) / 1_000_000d;
    }
}
