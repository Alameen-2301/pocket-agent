using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using PocketAgent.Core.Voice;

namespace PocketAgent.Windows.Voice;

/// <summary>Microphone capture at 16 kHz mono through the default Windows recording device.</summary>
internal sealed class NAudioRecorder : IAudioRecorder
{
    private readonly object _lock = new();
    private WaveInEvent? _wave;
    private MemoryStream _buffer = new();
    private Action<short[]>? _onFrame;

    public int SampleRate => 16000;
    public bool IsRecording { get; private set; }

    public static bool HasMicrophone
    {
        get
        {
            try { return WaveInEvent.DeviceCount > 0; }
            catch { return false; }
        }
    }

    public void Start(Action<short[]> onFrame)
    {
        WaveInEvent? old;
        lock (_lock)
        {
            old = _wave;
            _wave = null;
        }
        Release(old);
        if (!HasMicrophone) throw new InvalidOperationException("No microphone was found. Check Windows Settings → Privacy → Microphone.");
        var wave = new WaveInEvent
        {
            DeviceNumber = -1, // WAVE_MAPPER: the default recording device
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        };
        wave.DataAvailable += OnData;
        lock (_lock)
        {
            _buffer = new MemoryStream();
            _onFrame = onFrame;
            _wave = wave;
            IsRecording = true;
        }
        try { wave.StartRecording(); }
        catch
        {
            lock (_lock) { _wave = null; IsRecording = false; }
            Release(wave);
            throw;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        Action<short[]>? callback;
        lock (_lock)
        {
            if (!ReferenceEquals(sender, _wave)) return;
            _buffer.Write(e.Buffer, 0, e.BytesRecorded);
            callback = _onFrame;
        }
        if (callback is null || e.BytesRecorded < 2) return;
        var samples = new short[e.BytesRecorded / 2];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, samples.Length * 2);
        try { callback(samples); } catch { }
    }

    public byte[] Stop()
    {
        WaveInEvent? wave;
        byte[] pcm;
        lock (_lock)
        {
            wave = _wave;
            _wave = null;
            _onFrame = null;
            IsRecording = false;
            pcm = _buffer.ToArray();
        }
        Release(wave);
        return Wav.FromPcm16(pcm, SampleRate);
    }

    private void Release(WaveInEvent? wave)
    {
        if (wave is null) return;
        wave.DataAvailable -= OnData;
        try { wave.StopRecording(); } catch { }
        try { wave.Dispose(); } catch { }
    }
}

/// <summary>Plays WAV audio (speech) and can be cut off instantly when the user starts talking.</summary>
internal sealed class NAudioPlayer : IAudioPlayer
{
    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private TaskCompletionSource? _done;

    public bool IsPlaying { get { lock (_lock) return _output?.PlaybackState == PlaybackState.Playing; } }

    public async Task PlayAsync(byte[] wav, CancellationToken ct)
    {
        if (!Wav.TryRead(wav, out var info, out var pcm)) throw new InvalidDataException("The audio is not a PCM WAV file.");
        var format = new WaveFormat(info.SampleRate, info.BitsPerSample, info.Channels);
        using var stream = new RawSourceWaveStream(new MemoryStream(pcm.Array!, pcm.Offset, pcm.Count, writable: false), format);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new WaveOutEvent { DesiredLatency = 160, DeviceNumber = -1 };
        output.PlaybackStopped += (_, _) => done.TrySetResult();
        lock (_lock)
        {
            StopLocked();
            output.Init(stream);
            _output = output;
            _done = done;
            output.Play();
        }
        try
        {
            using (ct.Register(Stop)) await done.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) if (ReferenceEquals(_output, output)) _output = null;
            try { output.Dispose(); } catch { }
        }
    }

    public void Stop()
    {
        lock (_lock) StopLocked();
    }

    private void StopLocked()
    {
        var output = _output;
        _output = null;
        try { output?.Stop(); } catch { }
        _done?.TrySetResult();
        _done = null;
    }
}

/// <summary>Short, soft sounds for "listening", "done" and "error", generated in code.</summary>
internal sealed class CuePlayer
{
    private readonly NAudioPlayer _player = new();
    private readonly byte[] _listen = Tones((660, 70), (880, 90));
    private readonly byte[] _done = Tones((784, 60), (1046, 110));
    private readonly byte[] _error = Tones((330, 120), (247, 160));

    public void Play(string cue)
    {
        var sound = cue switch { "listen" => _listen, "done" => _done, "error" => _error, _ => null };
        if (sound is null) return;
        _ = Task.Run(async () =>
        {
            try { await _player.PlayAsync(sound, CancellationToken.None).ConfigureAwait(false); } catch { }
        });
    }

    private static byte[] Tones(params (double Hz, int Ms)[] parts)
    {
        const int rate = 24000;
        int total = 0;
        foreach (var p in parts) total += rate * p.Ms / 1000;
        var pcm = new byte[total * 2];
        int offset = 0;
        foreach (var (hz, ms) in parts)
        {
            int n = rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate;
                double envelope = Math.Min(1, i / (rate * 0.008)) * Math.Min(1, (n - i) / (rate * 0.02));
                short s = (short)(Math.Sin(2 * Math.PI * hz * t) * envelope * 0.18 * short.MaxValue);
                pcm[offset++] = (byte)(s & 0xFF);
                pcm[offset++] = (byte)((s >> 8) & 0xFF);
            }
        }
        return Wav.FromPcm16(pcm, rate);
    }
}
