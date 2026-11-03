using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PocketAgent.Core.Providers;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.Voice;

/// <summary>Speech-to-text through any Whisper-compatible /audio/transcriptions endpoint (OpenAI, Groq, local servers).</summary>
public sealed class WhisperApiTranscriber : IBatchTranscriber
{
    private readonly string _baseUrl, _apiKey, _model;
    public WhisperApiTranscriber(string baseUrl, string apiKey, string model)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-transcribe" : model;
    }

    public async Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken ct)
    {
        var node = await HttpJson.SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "speech.wav");
            form.Add(new StringContent(_model), "model");
            form.Add(new StringContent("json"), "response_format");
            var iso = IsoLanguage(language);
            if (iso is not null) form.Add(new StringContent(iso), "language");
            var m = new HttpRequestMessage(HttpMethod.Post, HttpJson.JoinUrl(_baseUrl, "audio/transcriptions")) { Content = form };
            if (!string.IsNullOrWhiteSpace(_apiKey)) m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return m;
        }, ct, 2).ConfigureAwait(false);
        return (node.Str("text") ?? "").Trim();
    }

    /// <summary>"de-DE" → "de"; "auto" → null (let the service detect it).</summary>
    public static string? IsoLanguage(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag == "auto") return null;
        var two = tag.Split('-', '_')[0].ToLowerInvariant();
        return two.Length is 2 or 3 ? two : null;
    }
}

/// <summary>Text-to-speech through an OpenAI-compatible /audio/speech endpoint.</summary>
public sealed class OpenAiSpeechSynthesizer : ITextToSpeech
{
    private readonly string _baseUrl, _apiKey, _model, _voice;
    private readonly double _speed;
    public OpenAiSpeechSynthesizer(string baseUrl, string apiKey, string model, string voice, double speed)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini-tts" : model;
        _voice = string.IsNullOrWhiteSpace(voice) ? "alloy" : voice;
        _speed = Math.Clamp(speed, 0.5, 2.0);
    }

    public async Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["voice"] = _voice,
            ["input"] = text,
            ["response_format"] = "wav",
        };
        if (Math.Abs(_speed - 1.0) > 0.01) body["speed"] = _speed;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Post, HttpJson.JoinUrl(_baseUrl, "audio/speech"))
        {
            Content = HttpJson.JsonContent(body),
        };
        if (!string.IsNullOrWhiteSpace(_apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await HttpJson.Client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text2 = System.Text.Encoding.UTF8.GetString(bytes);
            throw new ProviderException($"Speech service error {(int)response.StatusCode}: {HttpJson.ExtractError(text2) ?? response.ReasonPhrase}", (int)response.StatusCode, text2);
        }
        if (!Wav.TryRead(bytes, out _, out _)) throw new ProviderException("The speech service did not return WAV audio.");
        return bytes;
    }
}
