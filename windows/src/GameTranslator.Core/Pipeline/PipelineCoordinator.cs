using System.Diagnostics;

namespace GameTranslator.Core.Pipeline;

public abstract record PipelineEvent
{
    public sealed record Recognized(string Text, double OcrMs) : PipelineEvent;
    public sealed record Translating(string Source) : PipelineEvent;
    public sealed record Subtitle(string Source, string Target, string Engine, bool Cached, double TranslateMs) : PipelineEvent;
    public sealed record Cleared : PipelineEvent;
}

/// <summary>Điều phối: phát hiện thay đổi, OCR, chuẩn hóa, cache, dịch.
/// Chỉ xử lý một khung một lúc; khung đến khi đang bận bị bỏ. Dịch chạy ở Task riêng nên không chặn khung kế tiếp.</summary>
public sealed class PipelineCoordinator
{
    /// <summary>Khi đang hiện phụ đề, cứ mỗi chừng này ms lại OCR kiểm tra chữ còn đó không.</summary>
    internal const double PresencePollMs = 600;
    internal const int MissesToHide = 2;

    private readonly IOcrEngine _ocr;
    private readonly TranslationCache _cache;
    private readonly Action<PipelineEvent> _emit;
    private readonly Func<double> _clockMs;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);

    private bool _enabled;
    private bool _busy;

    private Profile _profile = Profile.Default;
    private readonly ChangeDetector _detector = new();
    private FallbackTranslator? _translator;
    private readonly List<ContextPair> _recentPairs = [];
    private string? _lastSource;
    private int _generation;
    private CancellationTokenSource? _translateCts;
    private double _lastPollMs;
    private int _missCount;

    public PipelineCoordinator(IOcrEngine ocr, TranslationCache cache, Action<PipelineEvent> emit, Func<double>? clockMs = null)
    {
        _ocr = ocr;
        _cache = cache;
        _emit = emit;
        var sw = Stopwatch.StartNew();
        _clockMs = clockMs ?? (() => sw.Elapsed.TotalMilliseconds);
    }

    public void SetEnabled(bool enabled) { lock (_gate) _enabled = enabled; }

    /// <summary>Capture hỏi trước khi cắt vùng: false thì khỏi tốn công chép ảnh.</summary>
    public bool WantsFrame { get { lock (_gate) return _enabled && !_busy; } }

    /// <summary>Gọi từ luồng capture/vẽ. Không chờ: nếu pipeline bận thì bỏ khung.</summary>
    public void Submit(IReadOnlyList<BgraImage> regions)
    {
        lock (_gate)
        {
            if (!_enabled || _busy) return;
            _busy = true;
        }
        _ = Task.Run(async () =>
        {
            try { await ProcessAsync(regions).ConfigureAwait(false); }
            catch (Exception) { /* bỏ khung này */ }
            finally { lock (_gate) _busy = false; }
        });
    }

    public Profile Profile => _profile;

    public void Configure(Profile profile, FallbackTranslator translator)
    {
        _serial.Wait();
        try
        {
            bool changed = _profile.Id != profile.Id || !_profile.AllRegions.SequenceEqual(profile.AllRegions);
            _profile = profile;
            _translator = translator;
            _detector.Settings = new ChangeDetector.Config(
                profile.Detector.ChangeThreshold, profile.Detector.StableMs, profile.Detector.HideDelayMs);
            if (changed) ResetStateLocked();
        }
        finally { _serial.Release(); }
    }

    public void ResetState()
    {
        _serial.Wait();
        try { ResetStateLocked(); } finally { _serial.Release(); }
    }

    private void ResetStateLocked()
    {
        _detector.Reset();
        _lastSource = null;
        _missCount = 0;
        _recentPairs.Clear();
        _translateCts?.Cancel();
        _generation++;
    }

    /// <summary>Dịch lại câu hiện tại, bỏ qua cache.</summary>
    public void Retranslate()
    {
        _serial.Wait();
        try { if (_lastSource is { } s) StartTranslation(s, bypassCache: true); }
        finally { _serial.Release(); }
    }

    // --- Xử lý khung ---

    private async Task ProcessAsync(IReadOnlyList<BgraImage> regions)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            var samples = new List<byte>();
            foreach (var r in regions) samples.AddRange(Preprocessor.Thumbnail(r));
            double nowMs = _clockMs();

            switch (_detector.Process([.. samples], nowMs))
            {
                case ChangeDetector.Event.None:
                    if (_lastSource is not null && nowMs - _lastPollMs >= PresencePollMs)
                    {
                        _lastPollMs = nowMs;
                        await CheckPresenceAsync(regions).ConfigureAwait(false);
                    }
                    break;
                case ChangeDetector.Event.Cleared:
                    Hide();
                    break;
                case ChangeDetector.Event.TextStable:
                    _lastPollMs = nowMs;
                    await RecognizeAsync(regions).ConfigureAwait(false);
                    break;
            }
        }
        finally { _serial.Release(); }
    }

    private void Hide()
    {
        _lastSource = null;
        _missCount = 0;
        _detector.Reset();
        _emit(new PipelineEvent.Cleared());
    }

    /// <summary>OCR mọi vùng rồi ghép chữ lại. null nếu không đọc được chữ nào.</summary>
    private async Task<(string Text, double Ms)?> ReadTextAsync(IReadOnlyList<BgraImage> regions)
    {
        var words = _profile.Glossary.Keys.ToList();
        var parts = new List<string>();
        double totalMs = 0;
        foreach (var region in regions)
        {
            var prepared = Preprocessor.Prepare(region, _profile.Ocr);
            try
            {
                var result = await _ocr.RecognizeAsync(prepared, _profile.Ocr, words).ConfigureAwait(false);
                totalMs += result.ElapsedMs;
                if (TextNormalizer.NormalizeParagraphs(result.Text) is { } text) parts.Add(text);
            }
            catch (Exception) { /* bỏ vùng này */ }
        }
        return parts.Count == 0 ? null : (string.Join(' ', parts), totalMs);
    }

    /// <summary>Đang hiện phụ đề mà OCR liên tiếp không thấy chữ thì ẩn đi.</summary>
    private async Task CheckPresenceAsync(IReadOnlyList<BgraImage> regions)
    {
        if (await ReadTextAsync(regions).ConfigureAwait(false) is null)
        {
            _missCount++;
            if (_missCount >= MissesToHide) Hide();
        }
        else _missCount = 0;
    }

    private async Task RecognizeAsync(IReadOnlyList<BgraImage> regions)
    {
        if (await ReadTextAsync(regions).ConfigureAwait(false) is not var (text, ms))
        {
            if (_lastSource is not null) Hide();
            return;
        }
        _missCount = 0;
        if (_lastSource is { } last && TextNormalizer.IsSameSentence(last, text)) return;

        _lastSource = text;
        _emit(new PipelineEvent.Recognized(text, ms));
        StartTranslation(text, bypassCache: false);
    }

    private void StartTranslation(string source, bool bypassCache)
    {
        var profileId = _profile.Id;
        if (!bypassCache && _cache.Lookup(profileId, source) is { } hit)
        {
            _translateCts?.Cancel();
            _generation++;
            Remember(source, hit.Target);
            _emit(new PipelineEvent.Subtitle(source, hit.Target, hit.Engine, true, 0));
            return;
        }
        if (_translator is not { } translator) return;

        _emit(new PipelineEvent.Translating(source));
        _translateCts?.Cancel();
        var cts = _translateCts = new CancellationTokenSource();
        int current = ++_generation;
        var request = new TranslationRequest(
            source,
            _recentPairs.TakeLast(_profile.Translate.ContextLines).ToList(),
            new Dictionary<string, string>(_profile.Glossary));
        bool upgrade = _profile.Translate.UpgradeOffline;
        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var outcome = await translator.TranslateAsync(request, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;
            await FinishAsync(current, profileId, request, outcome, sw.Elapsed.TotalMilliseconds, translator, upgrade).ConfigureAwait(false);
        });
    }

    private async Task FinishAsync(int generation, string profileId, TranslationRequest request,
        TranslationOutcome outcome, double ms, FallbackTranslator translator, bool upgrade)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != _generation) return;
            var source = request.Text;
            if (outcome.Engine.Kind != EngineKind.Original)
            {
                _cache.Store(profileId, source, outcome.Text, outcome.Engine.Label);
                Remember(source, outcome.Text);
            }
            _emit(new PipelineEvent.Subtitle(source, outcome.Text, outcome.Engine.Label, false, ms));
        }
        finally { _serial.Release(); }

        if (outcome.Engine.Kind == EngineKind.Apple && upgrade)
        {
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                if (await translator.UpgradeAsync(request).ConfigureAwait(false) is not { } better) return;
                await ApplyUpgradeAsync(profileId, request.Text, better, sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);
            });
        }
    }

    /// <summary>Thay bản dịch offline bằng bản online; chỉ cập nhật phụ đề nếu câu đó vẫn đang hiện.</summary>
    private async Task ApplyUpgradeAsync(string profileId, string source, TranslationOutcome outcome, double ms)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            _cache.Store(profileId, source, outcome.Text, outcome.Engine.Label);
            if (_lastSource != source) return;
            _emit(new PipelineEvent.Subtitle(source, outcome.Text, outcome.Engine.Label, false, ms));
        }
        finally { _serial.Release(); }
    }

    private void Remember(string source, string target)
    {
        _recentPairs.Add(new ContextPair(source, target));
        if (_recentPairs.Count > 6) _recentPairs.RemoveRange(0, _recentPairs.Count - 6);
    }
}
