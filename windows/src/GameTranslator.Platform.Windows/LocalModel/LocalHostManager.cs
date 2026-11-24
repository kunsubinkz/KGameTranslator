using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GameTranslator.Core;
using GameTranslator.Core.LocalHost;

namespace GameTranslator.Platform.Windows.LocalModel;

public enum HostState { NotInstalled, NoModel, Stopped, Starting, Loading, Ready, Error }

public sealed record HostStatus(HostState State, string Text, string? Device = null, string? Model = null);

/// <summary>Cài môi trường, tải model, chạy/dừng host NLLB và báo trạng thái. Không phụ thuộc giao diện.</summary>
public sealed class LocalHostManager : IDisposable
{
    public const string TokenAccount = "nllb-host";

    private readonly NllbPaths _paths;
    private readonly string _logFile;
    private readonly Func<string> _modelId;
    private readonly PythonEnvironment _env;
    private readonly ModelDownloader _downloader = new();
    private readonly object _lock = new();
    private readonly Queue<string> _tail = new();
    private Process? _proc;
    private JobObject? _job;
    private CancellationTokenSource? _pollCts;
    private HostStatus _status = new(HostState.NotInstalled, "Chưa cài");
    private int _restarts;
    private bool _stopping;
    private string? _lastError;

    public LocalHostManager(NllbPaths paths, string logFile, Func<string> modelId)
    {
        _paths = paths;
        _logFile = logFile;
        _modelId = modelId;
        _env = new PythonEnvironment(paths);
        _status = Compute(HostState.Stopped, "Đã dừng");
    }

    public NllbPaths Paths => _paths;
    public PythonEnvironment Environment => _env;
    public ModelDownloader Downloader => _downloader;
    public int Port { get; private set; } = 47811;
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string Device { get; set; } = "auto";
    public bool IsRunning => _proc is { HasExited: false };
    public HostStatus Status => _status;
    public event Action<HostStatus>? StatusChanged;

    /// <summary>Token ngẫu nhiên, lưu trong Credential Manager, không bao giờ ghi log.</summary>
    public static string Token()
    {
        var t = CredentialStore.Read(TokenAccount);
        if (!string.IsNullOrEmpty(t)) return t;
        t = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        CredentialStore.Write(TokenAccount, t);
        return t;
    }

    public IReadOnlyList<string> LogTail() { lock (_tail) return [.. _tail]; }

    public bool ModelDownloaded(string repoId) => ModelDownloader.IsComplete(_paths.ModelDir(repoId));

    private HostStatus Compute(HostState fallback, string text)
    {
        if (!_env.IsInstalled) return new(HostState.NotInstalled, "Dịch offline chưa được cài");
        if (!ModelDownloaded(_modelId())) return new(HostState.NoModel, "Chưa có model");
        return new(fallback, text);
    }

    private void Set(HostStatus s)
    {
        _status = s;
        StatusChanged?.Invoke(s);
    }

    public void Refresh()
    {
        if (!IsRunning) Set(Compute(HostState.Stopped, "Đã dừng"));
    }

    // ---------- Cài một nút ----------

    /// <summary>Cài Python và gói theo GPU, tải model mặc định, khởi động host. Làm tiếp được nếu bị ngắt.</summary>
    public async Task InstallAllAsync(string backend, string repoId, Action<string, double?> progress, CancellationToken ct)
    {
        if (!_env.IsInstalled)
        {
            progress("Cài môi trường Python...", null);
            await _env.InstallAsync(backend, l => progress(l, null), ct).ConfigureAwait(false);
        }
        if (!ModelDownloaded(repoId))
        {
            await _downloader.DownloadAsync(repoId, _paths.ModelDir(repoId), p =>
                progress($"Tải model {p.File} ({p.FileIndex}/{p.FileCount}): {p.Done / 1048576} / {p.Total / 1048576} MB",
                    p.Total > 0 ? (double)p.Done / p.Total : null), ct).ConfigureAwait(false);
        }
        progress("Khởi động host dịch...", null);
        await StartAsync(ct).ConfigureAwait(false);
    }

    // ---------- Chạy / dừng ----------

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;
        if (!_env.IsInstalled) { Set(new(HostState.NotInstalled, "Dịch offline chưa được cài")); return; }
        var repo = _modelId();
        if (!ModelDownloaded(repo)) { Set(Compute(HostState.NoModel, "")); return; }

        _env.SyncHostFiles();
        _stopping = false;
        Port = FindFreePort(Port);
        var token = Token();
        Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);

        var psi = new ProcessStartInfo(_paths.VenvPython)
        {
            WorkingDirectory = _paths.HostDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-I", _paths.HostScript, "serve", "--port", Port.ToString(), "--model-dir", _paths.ModelDir(repo), "--device", Device })
            psi.ArgumentList.Add(a);
        psi.Environment["GT_HOST_TOKEN"] = token;
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["HF_HUB_OFFLINE"] = "1";
        psi.Environment["TRANSFORMERS_OFFLINE"] = "1";

        Set(new(HostState.Starting, "Đang khởi động"));
        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => OnLog(e.Data);
            p.ErrorDataReceived += (_, e) => OnLog(e.Data);
            p.Exited += (_, _) => OnExited(p);
            p.Start();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            _job ??= new JobObject();
            _job.Assign(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Set(new(HostState.Error, "Không chạy được host: " + ex.Message));
            return;
        }
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        _ = PollAsync(_pollCts.Token);
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stopping = true;
        _pollCts?.Cancel();
        var p = _proc;
        if (p is { HasExited: false })
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/shutdown");
                req.Headers.Authorization = new("Bearer", Token());
                await http.SendAsync(req).ConfigureAwait(false);
            }
            catch (Exception) { }
            try { await p.WaitForExitAsync(new CancellationTokenSource(5000).Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch (Exception) { } }
        }
        _proc = null;
        Set(Compute(HostState.Stopped, "Đã dừng"));
    }

    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _restarts = 0;
        await StartAsync().ConfigureAwait(false);
    }

    private void OnLog(string? line)
    {
        if (line is null) return;
        lock (_tail)
        {
            _tail.Enqueue(line);
            while (_tail.Count > 200) _tail.Dequeue();
        }
        try { lock (_lock) File.AppendAllText(_logFile, line + "\n"); } catch (Exception) { }
    }

    private void OnExited(Process p)
    {
        if (_stopping || !ReferenceEquals(p, _proc)) return;
        _proc = null;
        _pollCts?.Cancel();
        if (_restarts++ < 3)
        {
            Set(new(HostState.Starting, $"Host thoát, thử lại lần {_restarts}/3"));
            _ = StartAsync();
        }
        else
        {
            var tail = string.Join(" | ", LogTail().TakeLast(3));
            Set(new(HostState.Error, "Host dừng đột ngột. " + tail));
        }
    }

    /// <summary>Hỏi /v1/health mỗi 1,5 giây để cập nhật trạng thái.</summary>
    private async Task PollAsync(CancellationToken ct)
    {
        var tr = new LocalHostTranslator(() => BaseUrl, Token, null, new HttpClient { Timeout = TimeSpan.FromSeconds(3) });
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var h = await tr.HealthAsync(ct).ConfigureAwait(false);
                switch (h.State)
                {
                    case "ready":
                        _restarts = 0;
                        Set(new(HostState.Ready, "Sẵn sàng", h.Device, h.Model));
                        break;
                    case "error":
                        Set(new(HostState.Error, "Lỗi nạp model: " + h.Error));
                        break;
                    default:
                        Set(new(HostState.Loading, "Đang nạp model", h.Device, h.Model));
                        break;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                if (_status.State is not (HostState.Error or HostState.Ready)) Set(new(HostState.Starting, "Đang khởi động"));
                else if (_status.State == HostState.Ready) Set(new(HostState.Starting, "Mất kết nối host"));
            }
            try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Cổng ưu tiên nếu trống, không thì cổng trống do hệ điều hành chọn.</summary>
    public static int FindFreePort(int preferred)
    {
        foreach (var port in new[] { preferred, 0 })
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                var p = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return p;
            }
            catch (SocketException) { }
        }
        return preferred;
    }

    public void Dispose()
    {
        _stopping = true;
        _pollCts?.Cancel();
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch (Exception) { }
        _job?.Dispose();
    }
}
