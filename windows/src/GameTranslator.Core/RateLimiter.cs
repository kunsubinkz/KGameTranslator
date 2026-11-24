using System.Globalization;

namespace GameTranslator.Core;

/// <summary>Token bucket theo phút kèm bộ đếm theo ngày (đặt lại lúc nửa đêm giờ Thái Bình Dương).</summary>
public sealed class RateLimiter
{
    private static readonly TimeZoneInfo PacificZone = FindPacific();

    private readonly object _lock = new();
    private readonly double _perMinute;
    private readonly int _perDay;
    private double _tokens;
    private DateTimeOffset _lastRefill;
    private string _dayKey;
    private int _dayCount;
    private readonly Action<string, int>? _persist;

    public RateLimiter(int perMinute, int perDay, DateTimeOffset? now = null,
        (string DayKey, int Count)? restored = null, Action<string, int>? persist = null)
    {
        var start = now ?? DateTimeOffset.UtcNow;
        _perMinute = Math.Max(perMinute, 1);
        _perDay = Math.Max(perDay, 1);
        _tokens = _perMinute;
        _lastRefill = start;
        _persist = persist;
        var today = DayKeyFor(start);
        _dayKey = today;
        _dayCount = restored is { } r && r.DayKey == today ? r.Count : 0;
    }

    private static TimeZoneInfo FindPacific()
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.Utc;
    }

    public static string DayKeyFor(DateTimeOffset date)
    {
        var local = TimeZoneInfo.ConvertTime(date, PacificZone);
        return string.Create(CultureInfo.InvariantCulture, $"{local.Year:D4}-{local.Month:D2}-{local.Day:D2}");
    }

    /// <summary>Thử lấy một lượt gọi API. Trả về false nếu hết hạn mức phút hoặc ngày.</summary>
    public bool TryAcquire(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            RollDayIfNeeded(t);
            Refill(t);
            if (_dayCount >= _perDay || _tokens < 1) return false;
            _tokens -= 1;
            _dayCount += 1;
            _persist?.Invoke(_dayKey, _dayCount);
            return true;
        }
    }

    public int RemainingToday(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            RollDayIfNeeded(t);
            return Math.Max(_perDay - _dayCount, 0);
        }
    }

    public int DailyLimit => _perDay;

    private void Refill(DateTimeOffset now)
    {
        double elapsed = Math.Max((now - _lastRefill).TotalSeconds, 0);
        _tokens = Math.Min(_perMinute, _tokens + elapsed * _perMinute / 60);
        _lastRefill = now;
    }

    private void RollDayIfNeeded(DateTimeOffset now)
    {
        var key = DayKeyFor(now);
        if (key != _dayKey)
        {
            _dayKey = key;
            _dayCount = 0;
            _persist?.Invoke(_dayKey, _dayCount);
        }
    }
}
