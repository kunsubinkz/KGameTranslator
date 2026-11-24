namespace GameTranslator.Core;

/// <summary>Điều phối AI online (Claude hoặc Gemini), dịch offline và chữ gốc theo chế độ người dùng chọn.</summary>
public sealed class FallbackTranslator(
    TranslateMode mode,
    ITranslator? online,
    string onlineModel,
    TranslateProvider provider = TranslateProvider.Claude,
    ITranslator? offline = null,
    RateLimiter? limiter = null,
    Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly object _lock = new();
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private double _backoffSeconds;

    public TranslateMode Mode { get; } = mode;

    public async Task<TranslationOutcome> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
    {
        if (Mode != TranslateMode.OfflineOnly && await TryOnlineAsync(request, ct).ConfigureAwait(false) is { } text)
            return new TranslationOutcome(text, OnlineEngine);
        if (ct.IsCancellationRequested) return new TranslationOutcome(request.Text, TranslationEngine.Original);
        if (Mode != TranslateMode.OnlineOnly && offline is not null)
        {
            try
            {
                var t = await offline.TranslateAsync(request, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(t)) return new TranslationOutcome(t, TranslationEngine.Apple);
            }
            catch (Exception) { }
        }
        return new TranslationOutcome(request.Text, TranslationEngine.Original);
    }

    /// <summary>Dịch lại bằng AI online để thay bản offline. Trả về null nếu hết hạn mức hoặc lỗi.</summary>
    public async Task<TranslationOutcome?> UpgradeAsync(TranslationRequest request, CancellationToken ct = default)
    {
        if (Mode == TranslateMode.OfflineOnly) return null;
        return await TryOnlineAsync(request, ct).ConfigureAwait(false) is { } text
            ? new TranslationOutcome(text, OnlineEngine)
            : null;
    }

    private TranslationEngine OnlineEngine => provider == TranslateProvider.Claude
        ? TranslationEngine.Claude(onlineModel)
        : TranslationEngine.Gemini(onlineModel);

    private async Task<string?> TryOnlineAsync(TranslationRequest request, CancellationToken ct)
    {
        if (online is null) return null;
        lock (_lock)
        {
            if (_now() < _backoffUntil) return null;
        }
        if (limiter is not null && !limiter.TryAcquire(_now())) return null;
        try
        {
            var text = await online.TranslateAsync(request, ct).ConfigureAwait(false);
            lock (_lock) _backoffSeconds = 0;
            return text;
        }
        catch (TranslationException ex) when (ex.Kind == TranslationErrorKind.QuotaExceeded)
        {
            lock (_lock)
            {
                _backoffSeconds = Math.Min(Math.Max(_backoffSeconds * 2, 1), 30);
                _backoffUntil = _now().AddSeconds(_backoffSeconds);
            }
            return null;
        }
        catch (Exception)
        {
            // Lỗi mạng, timeout, phản hồi rỗng: chuyển offline ngay, câu sau thử lại.
            return null;
        }
    }
}
