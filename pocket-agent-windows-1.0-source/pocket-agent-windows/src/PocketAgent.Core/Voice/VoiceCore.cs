using System.Buffers.Binary;
using System.Text;

namespace PocketAgent.Core.Voice;

/// <summary>Microphone capture (16 kHz, 16-bit, mono).</summary>
public interface IAudioRecorder
{
    bool IsRecording { get; }
    /// <summary>Starts capturing; <paramref name="onFrame"/> receives PCM samples on an audio thread.</summary>
    void Start(Action<short[]> onFrame);
    /// <summary>Stops and returns everything captured as a WAV file.</summary>
    byte[] Stop();
    int SampleRate { get; }
}

public interface IAudioPlayer
{
    bool IsPlaying { get; }
    /// <summary>Plays a WAV file; completes when playback ends or is stopped.</summary>
    Task PlayAsync(byte[] wav, CancellationToken ct);
    void Stop();
}

/// <summary>A recogniser that listens to the microphone itself and can show partial text (Windows speech).</summary>
public interface ILiveSpeechRecognizer
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    Task StartAsync(string language, Action<string> onPartial, CancellationToken ct);
    Task<string> StopAsync();
    void Cancel();
}

/// <summary>A recogniser that transcribes a finished recording (Whisper-compatible APIs).</summary>
public interface IBatchTranscriber
{
    Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken ct);
}

public interface ITextToSpeech
{
    /// <summary>Returns a WAV file.</summary>
    Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken ct);
}

/// <summary>Offline phrase spotting for the wake word and "stop / pause / continue" (Windows SAPI).</summary>
public interface IWakeWordListener
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    void Configure(bool wakeWord, bool commands, string language);
    /// <summary>Temporarily ignore what it hears (while recording or speaking).</summary>
    void SetSuspended(bool suspended);
    event Action? WakeWordHeard;
    event Action<VoiceCommand>? CommandHeard;
}

public enum VoiceCommand { None, Stop, Pause, Resume }

public static class VoiceCommands
{
    private static readonly string[] StopPhrases =
        ["stop", "stop it", "stop now", "stop stop", "stop pocket", "pocket stop", "cancel", "cancel that", "abort", "halt",
         "stopp", "stopp stopp", "abbrechen", "anhalten", "halt stopp", "aufhören", "hör auf"];
    private static readonly string[] PausePhrases = ["pause", "wait", "hold on", "one moment", "warte", "warte mal", "pausieren", "moment"];
    private static readonly string[] ResumePhrases =
        ["continue", "resume", "go on", "go ahead", "keep going", "carry on", "weiter", "mach weiter", "fortfahren", "weitermachen"];

    /// <summary>Recognises a short spoken control command; ordinary requests return None.</summary>
    public static VoiceCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return VoiceCommand.None;
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant()) sb.Append(char.IsLetter(c) || c == ' ' ? c : ' ');
        var norm = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var filler in new[] { "okay ", "ok ", "alright ", "now ", "jetzt ", "also " })
            if (norm.StartsWith(filler)) norm = norm[filler.Length..];
        if (norm.StartsWith("please ")) norm = norm[7..];
        if (norm.EndsWith(" please")) norm = norm[..^7];
        if (norm.StartsWith("bitte ")) norm = norm[6..];
        if (norm.EndsWith(" bitte")) norm = norm[..^6];
        if (norm.StartsWith("hey pocket ")) norm = norm[11..];
        if (norm.StartsWith("pocket ") && norm != "pocket stop") norm = norm[7..];
        if (norm.Split(' ').Length > 3) return VoiceCommand.None;
        if (StopPhrases.Contains(norm)) return VoiceCommand.Stop;
        if (PausePhrases.Contains(norm)) return VoiceCommand.Pause;
        if (ResumePhrases.Contains(norm)) return VoiceCommand.Resume;
        return VoiceCommand.None;
    }
}

public sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample);

public static class Wav
{
    public static byte[] FromPcm16(ReadOnlySpan<byte> pcm, int sampleRate, int channels = 1)
    {
        var wav = new byte[44 + pcm.Length];
        var s = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(s[8..]);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], pcm.Length);
        pcm.CopyTo(s[44..]);
        return wav;
    }

    /// <summary>
    /// Reads PCM from a WAV file, tolerating streaming headers whose sizes are 0 or 0xFFFFFFFF
    /// (common in TTS API output).
    /// </summary>
    public static bool TryRead(byte[] wav, out WavInfo info, out ArraySegment<byte> pcm)
    {
        info = new WavInfo(0, 0, 0);
        pcm = default;
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return false;
        int pos = 12;
        bool haveFormat = false;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4));
            int body = pos + 8;
            if (id == "fmt " && body + 16 <= wav.Length)
            {
                int format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body));
                int channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 2));
                int rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(body + 4));
                int bits = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 14));
                // 1 = PCM, 0xFFFE = extensible (PCM sub-format in practice).
                if (format != 1 && format != 0xFFFE) return false;
                info = new WavInfo(rate, channels, bits);
                haveFormat = true;
            }
            else if (id == "data")
            {
                if (!haveFormat) return false;
                long available = wav.Length - body;
                long length = size == 0 || size == uint.MaxValue || size > available ? available : size;
                pcm = new ArraySegment<byte>(wav, body, (int)length);
                return true;
            }
            if (size == uint.MaxValue) return false;
            long next = (long)body + size + (size % 2);
            if (next > wav.Length) break;
            pos = (int)next;
        }
        return false;
    }

    public static double DurationSeconds(byte[] wav)
        => TryRead(wav, out var info, out var pcm) && info.SampleRate > 0
            ? pcm.Count / (double)(info.SampleRate * Math.Max(1, info.Channels) * Math.Max(1, info.BitsPerSample / 8))
            : 0;
}

public enum VadEvent { None, SpeechStarted, Ended, NoSpeech }

/// <summary>
/// Simple energy-based end-of-speech detection with an adaptive noise floor. Used for the wake word
/// and click-to-talk, where nobody holds a key down.
/// </summary>
public sealed class VoiceActivityDetector
{
    private readonly int _sampleRate;
    private readonly int _frameSamples;
    private readonly List<short> _carry = new();
    private double _noiseDb = -60;
    private int _frames;
    private int _speechRun;
    private int _silenceRun;
    private bool _speaking;
    private bool _done;

    public VoiceActivityDetector(int sampleRate = 16000, int trailingSilenceMs = 900, int noSpeechTimeoutMs = 7000, int maxUtteranceMs = 30000)
    {
        _sampleRate = sampleRate;
        _frameSamples = sampleRate / 50; // 20 ms
        TrailingSilenceFrames = Math.Max(1, trailingSilenceMs / 20);
        NoSpeechFrames = Math.Max(1, noSpeechTimeoutMs / 20);
        MaxFrames = Math.Max(1, maxUtteranceMs / 20);
    }

    public int TrailingSilenceFrames { get; }
    public int NoSpeechFrames { get; }
    public int MaxFrames { get; }
    public bool SpeechDetected => _speaking || _speechRun > 0;
    public double LastLevel { get; private set; }

    public VadEvent Process(ReadOnlySpan<short> samples)
    {
        if (_done) return VadEvent.None;
        var result = VadEvent.None;
        _carry.AddRange(samples.ToArray());
        while (_carry.Count >= _frameSamples)
        {
            var frame = _carry.GetRange(0, _frameSamples);
            _carry.RemoveRange(0, _frameSamples);
            var e = Frame(frame);
            if (e != VadEvent.None) result = e;
            if (_done) break;
        }
        return result;
    }

    private const int CalibrationFrames = 8; // 160 ms of room noise before speech can start

    private VadEvent Frame(List<short> frame)
    {
        _frames++;
        double db = Level.Dbfs(frame);
        LastLevel = Level.FromDb(db);
        if (_frames <= CalibrationFrames)
        {
            // Capped so that speech that starts immediately can't become the "noise floor".
            _noiseDb = Math.Min(-38, _frames == 1 ? db : Math.Max(_noiseDb, db));
            return VadEvent.None;
        }
        bool loud = db > Math.Max(-50, _noiseDb + 10);
        if (!_speaking)
        {
            // Keep tracking the room noise while nobody is talking.
            if (!loud) _noiseDb = _noiseDb * 0.95 + db * 0.05;
            _speechRun = loud ? _speechRun + 1 : 0;
            if (_speechRun >= 4)
            {
                _speaking = true;
                _silenceRun = 0;
                return VadEvent.SpeechStarted;
            }
            if (_frames >= NoSpeechFrames) { _done = true; return VadEvent.NoSpeech; }
            return VadEvent.None;
        }
        _silenceRun = loud ? 0 : _silenceRun + 1;
        if (_silenceRun >= TrailingSilenceFrames || _frames >= MaxFrames) { _done = true; return VadEvent.Ended; }
        return VadEvent.None;
    }
}

public static class Level
{
    public static double Dbfs(IReadOnlyList<short> samples)
    {
        if (samples.Count == 0) return -100;
        double sum = 0;
        for (int i = 0; i < samples.Count; i++) { double v = samples[i] / 32768.0; sum += v * v; }
        double rms = Math.Sqrt(sum / samples.Count);
        return rms <= 1e-6 ? -100 : 20 * Math.Log10(rms);
    }

    /// <summary>Maps -60..0 dBFS to 0..1 for meters.</summary>
    public static double FromDb(double db) => Math.Clamp((db + 60) / 60, 0, 1);

    public static double Of(short[] samples) => FromDb(Dbfs(samples));
}
