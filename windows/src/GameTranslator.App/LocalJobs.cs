using GameTranslator.Core.LocalHost;
using GameTranslator.Platform.Windows.LocalModel;

namespace GameTranslator.App;

/// <summary>Việc nền của dịch offline: cài một nút, tải model, cài lại môi trường. Một việc mỗi lúc.</summary>
public sealed class LocalJobs(Session s)
{
    private CancellationTokenSource? _cts;

    public bool Busy { get; private set; }
    public string Title { get; private set; } = "";
    public string Text { get; private set; } = "";
    public double? Fraction { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Model đang được tải (để hiện tiến trình ngay trên thẻ model).</summary>
    public string? CurrentRepo { get; private set; }
    public event Action? Changed;

    public static string BackendFor(Session s) =>
        s.Settings.Local.Backend is "xpu" or "cuda" or "cpu"
            ? s.Settings.Local.Backend
            : PythonEnvironment.SuggestBackend(s.Gpus.Select(g => g.Name), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64);

    public void Cancel() => _cts?.Cancel();

    /// <summary>Cài Python + gói theo GPU, tải model đã chọn, khởi động host.</summary>
    public Task InstallAllAsync(string? backend = null) => Run("Cài đặt dịch offline", async (ct, report) =>
    {
        CurrentRepo = s.Settings.Local.ModelId;
        backend ??= BackendFor(s);
        s.Settings.Local.Backend = backend;
        s.Settings.Save();
        await s.LocalHost.InstallAllAsync(backend, s.Settings.Local.ModelId, report, ct);
    });

    public Task DownloadModelAsync(string repo) => Run("Tải model " + repo, async (ct, report) =>
    {
        CurrentRepo = repo;
        await s.LocalHost.Downloader.DownloadAsync(repo, s.LocalHost.Paths.ModelDir(repo),
            p => report($"{p.File} ({p.FileIndex}/{p.FileCount}): {p.Done / 1048576} / {p.Total / 1048576} MB", p.Total > 0 ? (double)p.Done / p.Total : null), ct);
    });

    public Task ReinstallEnvAsync(string backend) => Run("Cài lại môi trường", async (ct, report) =>
    {
        await s.LocalHost.StopAsync();
        s.LocalHost.Environment.Uninstall();
        s.Settings.Local.Backend = backend;
        s.Settings.Save();
        await s.LocalHost.Environment.InstallAsync(backend, l => report(l, null), ct);
    });

    private async Task Run(string title, Func<CancellationToken, Action<string, double?>, Task> work)
    {
        if (Busy) return;
        CurrentRepo = null;
        Busy = true; Title = title; Text = "Bắt đầu..."; Fraction = null; Error = null;
        _cts = new CancellationTokenSource();
        Changed?.Invoke();
        try
        {
            await Task.Run(() => work(_cts.Token, (t, f) => { Text = t; Fraction = f; Changed?.Invoke(); }));
            Text = "Xong.";
            s.LocalHost.Refresh();
            s.ApplyProfile();
        }
        catch (OperationCanceledException) { Text = "Đã hủy. Bấm lại để làm tiếp từ chỗ dừng."; }
        catch (Exception ex) { Error = ex.Message; Text = "Lỗi."; }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }
}
