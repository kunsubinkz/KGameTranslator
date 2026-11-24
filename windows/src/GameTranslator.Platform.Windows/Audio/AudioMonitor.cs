using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameTranslator.Platform.Windows.Audio;

/// <summary>Nghe tiếng từ capture card (WASAPI capture) và phát ra loa/tai nghe (WASAPI render).
/// Chạy hoàn toàn riêng với hình, chu kỳ nhỏ để tiếng khớp hình.</summary>
public sealed class AudioMonitor : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _render;
    private BufferedWaveProvider? _buffer;
    private VolumeSampleProviderWrapper? _volume;
    private float _volumeValue = 1f;

    public event Action<string>? StatusChanged;

    public bool IsRunning => _capture is not null;

    public float Volume
    {
        get => _volumeValue;
        set
        {
            _volumeValue = Math.Clamp(value, 0f, 1f);
            if (_volume is not null) _volume.Volume = _volumeValue;
        }
    }

    /// <summary>`inputId`/`outputId` là ID endpoint WASAPI; outputId rỗng = thiết bị phát mặc định.
    /// `delayMs` (0–200) bù trễ để khớp tiếng với hình.</summary>
    public void Start(string inputId, string? outputId, int delayMs = 0)
    {
        Stop();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var input = enumerator.GetDevice(inputId);
            var output = string.IsNullOrEmpty(outputId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(outputId);

            _capture = new WasapiCapture(input, true, 20);
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            if (delayMs > 0)
            {
                int bytes = (int)(_capture.WaveFormat.AverageBytesPerSecond * (delayMs / 1000.0));
                bytes -= bytes % _capture.WaveFormat.BlockAlign;
                _buffer.AddSamples(new byte[bytes], 0, bytes);
            }
            var maxBuffered = TimeSpan.FromMilliseconds(delayMs + 250);
            _capture.DataAvailable += (_, e) =>
            {
                var b = _buffer;
                if (b is null) return;
                // Xung nhịp hai thiết bị lệch nhau thì bộ đệm phình ra làm tiếng trễ dần: xả bớt.
                if (b.BufferedDuration > maxBuffered + TimeSpan.FromMilliseconds(150)) b.ClearBuffer();
                b.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) StatusChanged?.Invoke("Tiếng: " + e.Exception.Message);
            };

            _volume = new VolumeSampleProviderWrapper(_buffer.ToSampleProvider()) { Volume = _volumeValue };
            _render = new WasapiOut(output, AudioClientShareMode.Shared, true, 30);
            _render.Init(_volume);
            _capture.StartRecording();
            _render.Play();
        }
        catch (Exception ex)
        {
            Stop();
            StatusChanged?.Invoke("Không mở được tiếng: " + ex.Message);
        }
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch (Exception) { }
        try { _render?.Stop(); } catch (Exception) { }
        _capture?.Dispose();
        _render?.Dispose();
        _capture = null;
        _render = null;
        _buffer = null;
        _volume = null;
    }

    public void Dispose() => Stop();

    private sealed class VolumeSampleProviderWrapper(ISampleProvider source) : ISampleProvider
    {
        public float Volume { get; set; } = 1f;
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = source.Read(buffer, offset, count);
            float v = Volume;
            if (v != 1f) for (int i = 0; i < n; i++) buffer[offset + i] *= v;
            return n;
        }
    }
}
