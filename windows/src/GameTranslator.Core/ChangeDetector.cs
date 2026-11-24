namespace GameTranslator.Core;

/// <summary>Phát hiện chữ mới và đã ổn định trong vùng hộp thoại. Thời gian được truyền từ ngoài vào để dễ test.</summary>
public sealed class ChangeDetector
{
    public enum Event
    {
        None,
        /// <summary>Chữ mới đã dừng thay đổi đủ lâu, nên OCR.</summary>
        TextStable,
        /// <summary>Hộp thoại đã đóng (vùng trống đủ lâu), nên ẩn phụ đề.</summary>
        Cleared,
    }

    public sealed record Config(
        double ChangeThreshold = 2.0,
        double StableMs = 350,
        double HideDelayMs = 800,
        /// <summary>Độ lệch chuẩn dưới ngưỡng này coi là vùng trống.</summary>
        double BlankStdDev = 4.0);

    public Config Settings { get; set; }

    private byte[]? _previous;
    private double _lastChangeMs;
    private bool _pendingStable;
    private bool _hasText;
    private double? _blankSinceMs;

    public ChangeDetector(Config? config = null) => Settings = config ?? new Config();

    public void Reset()
    {
        _previous = null;
        _pendingStable = false;
        _hasText = false;
        _blankSinceMs = null;
    }

    /// <summary>`samples` là ảnh xám thu nhỏ của vùng hộp thoại.</summary>
    public Event Process(byte[] samples, double nowMs)
    {
        if (samples.Length == 0) return Event.None;
        try
        {
            return Evaluate(samples, nowMs);
        }
        finally
        {
            _previous = samples;
        }
    }

    private Event Evaluate(byte[] samples, double nowMs)
    {
        if (StandardDeviation(samples) < Settings.BlankStdDev)
        {
            _pendingStable = false;
            if (!_hasText) return Event.None;
            double since = _blankSinceMs ?? nowMs;
            _blankSinceMs = since;
            if (nowMs - since >= Settings.HideDelayMs)
            {
                _hasText = false;
                _blankSinceMs = null;
                return Event.Cleared;
            }
            return Event.None;
        }
        _blankSinceMs = null;

        double diff = _previous is null ? double.PositiveInfinity : MeanAbsoluteDifference(_previous, samples);
        if (diff > Settings.ChangeThreshold)
        {
            _lastChangeMs = nowMs;
            _pendingStable = true;
            return Event.None;
        }
        if (_pendingStable && nowMs - _lastChangeMs >= Settings.StableMs)
        {
            _pendingStable = false;
            _hasText = true;
            return Event.TextStable;
        }
        return Event.None;
    }

    public static double MeanAbsoluteDifference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return double.PositiveInfinity;
        long total = 0;
        for (int i = 0; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
        return (double)total / a.Length;
    }

    public static double StandardDeviation(byte[] values)
    {
        double n = values.Length;
        double sum = 0, sumSquares = 0;
        foreach (var v in values)
        {
            double d = v;
            sum += d;
            sumSquares += d * d;
        }
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sumSquares / n - mean * mean));
    }
}
