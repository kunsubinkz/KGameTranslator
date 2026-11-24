using System.Text.Json;

namespace GameTranslator.Platform.Windows.LocalModel;

/// <summary>Kết quả lệnh `nllb_host.py check`, lưu ở env.json.</summary>
public sealed record EnvInfo(string Backend, string? Torch, string? Transformers, string? Python, bool Xpu, bool Cuda, string? DeviceName);

/// <summary>Cài Python + torch bằng uv vào thư mục của app (không đụng Python hệ thống, không cần admin).</summary>
public sealed class PythonEnvironment(NllbPaths paths)
{
    public const string PythonVersion = "3.12";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public bool IsInstalled => File.Exists(paths.VenvPython) && File.Exists(paths.EnvJson);

    public EnvInfo? Info()
    {
        try { return File.Exists(paths.EnvJson) ? JsonSerializer.Deserialize<EnvInfo>(File.ReadAllText(paths.EnvJson), Json) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Chép nllb_host.py và requirements-*.txt từ thư mục kèm app sang thư mục dữ liệu; khác nội dung thì chép đè.</summary>
    public void SyncHostFiles()
    {
        Directory.CreateDirectory(paths.HostDir);
        if (!Directory.Exists(paths.BundleDir)) return;
        foreach (var f in Directory.EnumerateFiles(paths.BundleDir).Where(f => f.EndsWith(".py") || f.EndsWith(".txt")))
        {
            var dest = Path.Combine(paths.HostDir, Path.GetFileName(f));
            if (!File.Exists(dest) || !File.ReadAllBytes(f).AsSpan().SequenceEqual(File.ReadAllBytes(dest)))
                File.Copy(f, dest, overwrite: true);
        }
    }

    private Dictionary<string, string> UvEnv() => new()
    {
        ["UV_PYTHON_INSTALL_DIR"] = paths.PythonDir,
        ["UV_CACHE_DIR"] = Path.Combine(paths.Root, "uv-cache"),
        ["UV_PYTHON_PREFERENCE"] = "only-managed",
        ["UV_NO_PROGRESS"] = "1",
    };

    /// <summary>Cài Python 3.12, tạo venv, cài gói theo backend (xpu | cuda | cpu), rồi chạy `check`.</summary>
    public async Task<EnvInfo> InstallAsync(string backend, Action<string> progress, CancellationToken ct)
    {
        if (!File.Exists(paths.UvExe))
            throw new FileNotFoundException("Thiếu uv.exe cạnh app: " + paths.UvExe);
        Directory.CreateDirectory(paths.Root);
        SyncHostFiles();
        var req = paths.Requirements(backend);
        if (!File.Exists(req)) throw new FileNotFoundException("Thiếu file " + req);
        var env = UvEnv();

        progress("Cài Python " + PythonVersion + "...");
        await Run(["python", "install", PythonVersion], env, progress, ct);
        if (!File.Exists(paths.VenvPython))
        {
            progress("Tạo môi trường ảo...");
            await Run(["venv", paths.Venv, "--python", PythonVersion], env, progress, ct);
        }
        progress($"Cài PyTorch ({backend}) và transformers (vài GB, mất vài phút)...");
        await Run(["pip", "install", "--python", paths.VenvPython, "-r", req], env, progress, ct);

        progress("Kiểm tra thiết bị...");
        var info = await CheckAsync(backend, ct) ?? throw new InvalidOperationException("Không đọc được kết quả kiểm tra môi trường.");
        File.WriteAllText(paths.EnvJson, JsonSerializer.Serialize(info, Json));
        return info;
    }

    private async Task Run(string[] args, Dictionary<string, string> env, Action<string> progress, CancellationToken ct)
    {
        var tail = new Queue<string>();
        var code = await ProcessRunner.RunAsync(paths.UvExe, args, l =>
        {
            if (string.IsNullOrWhiteSpace(l)) return;
            progress(l.Trim());
            lock (tail) { tail.Enqueue(l); if (tail.Count > 8) tail.Dequeue(); }
        }, ct, env);
        if (code != 0)
            throw new InvalidOperationException($"uv {args[0]} lỗi (mã {code}): " + string.Join(" | ", tail));
    }

    public async Task<EnvInfo?> CheckAsync(string backend, CancellationToken ct)
    {
        string? line = null;
        var code = await ProcessRunner.RunAsync(paths.VenvPython, ["-I", paths.HostScript, "check"], l => { if (l.TrimStart().StartsWith('{')) line = l; }, ct);
        if (line is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var err)) throw new InvalidOperationException(err.GetString());
            if (code != 0) return null;
            string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool Bool(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            var dev = r.TryGetProperty("devices", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0
                ? d[0].GetProperty("name").GetString() : null;
            return new EnvInfo(backend, Str("torch"), Str("transformers"), Str("python"), Bool("xpu"), Bool("cuda"), dev);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Xóa venv và env.json (giữ Python và model).</summary>
    public void Uninstall()
    {
        if (Directory.Exists(paths.Venv)) Directory.Delete(paths.Venv, recursive: true);
        if (File.Exists(paths.EnvJson)) File.Delete(paths.EnvJson);
    }

    /// <summary>Backend gợi ý theo GPU: Intel Arc → xpu, NVIDIA → cuda, còn lại cpu (ARM64 luôn cpu).</summary>
    public static string SuggestBackend(IEnumerable<string> gpuNames, bool arm64)
    {
        if (arm64) return "cpu";
        var names = gpuNames.ToList();
        if (names.Any(n => n.Contains("Arc", StringComparison.OrdinalIgnoreCase))) return "xpu";
        if (names.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase))) return "cuda";
        return "cpu";
    }
}
