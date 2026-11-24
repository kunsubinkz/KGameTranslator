using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using GameTranslator.Core.LocalHost;

namespace GameTranslator.Platform.Windows.LocalModel;

public sealed record DownloadProgress(string File, long Done, long Total, int FileIndex, int FileCount);

/// <summary>Tải model từ Hugging Face vào thư mục thường: tải tiếp bằng Range, kiểm SHA-256 với `lfs.oid`.</summary>
public sealed class ModelDownloader(HttpClient? http = null, string baseUrl = "https://huggingface.co")
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public sealed record RemoteFile(string Path, long Size, string? Sha256);

    /// <summary>Danh sách file cần tải (config, tokenizer và trọng số; bỏ các bản trọng số thừa).</summary>
    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string repoId, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{baseUrl}/api/models/{repoId}/tree/main", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Không lấy được danh sách file ({(int)resp.StatusCode}).");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var all = new List<RemoteFile>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.GetProperty("type").GetString() != "file") continue;
            var path = e.GetProperty("path").GetString()!;
            var size = e.GetProperty("size").GetInt64();
            string? oid = null;
            if (e.TryGetProperty("lfs", out var lfs))
            {
                oid = lfs.GetProperty("oid").GetString();
                size = lfs.GetProperty("size").GetInt64();
            }
            all.Add(new RemoteFile(path, size, oid));
        }
        return Select(all);
    }

    public static IReadOnlyList<RemoteFile> Select(IReadOnlyList<RemoteFile> all)
    {
        bool Has(Func<string, bool> p) => all.Any(f => p(f.Path));
        Func<string, bool> weights =
            Has(p => p.EndsWith(".safetensors")) ? p => p.EndsWith(".safetensors") || p == "model.safetensors.index.json"
            : p => (p.StartsWith("pytorch_model") && p.EndsWith(".bin")) || p == "pytorch_model.bin.index.json";
        return [.. all.Where(f => LocalModelCatalog.SmallFiles.Contains(f.Path) || weights(f.Path))];
    }

    /// <summary>Đã tải xong (có dấu .complete) hoặc là thư mục model nhập từ ngoài (có config và trọng số, không còn .part).</summary>
    public static bool IsComplete(string modelDir)
    {
        if (!File.Exists(Path.Combine(modelDir, "config.json"))) return false;
        if (File.Exists(Path.Combine(modelDir, ".complete"))) return true;
        try
        {
            var files = Directory.EnumerateFiles(modelDir).Select(Path.GetFileName).ToList();
            return !files.Any(f => f!.EndsWith(".part")) && files.Any(f => f!.EndsWith(".safetensors") || (f!.StartsWith("pytorch_model") && f.EndsWith(".bin")));
        }
        catch (Exception) { return false; }
    }

    /// <summary>Tải model vào <paramref name="destDir"/>. Hủy hoặc đứt mạng thì giữ file .part để tải tiếp.</summary>
    public async Task DownloadAsync(string repoId, string destDir, Action<DownloadProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var files = await ListAsync(repoId, ct).ConfigureAwait(false);
        var total = files.Sum(f => f.Size);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destDir))!).AvailableFreeSpace;
        if (free < total) throw new IOException($"Ổ đĩa không đủ chỗ: cần {total / 1048576} MB, còn {free / 1048576} MB.");
        var doneBefore = 0L;
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            await DownloadFileAsync(repoId, f, destDir, bytes => progress(new DownloadProgress(f.Path, doneBefore + bytes, total, i + 1, files.Count)), ct)
                .ConfigureAwait(false);
            doneBefore += f.Size;
        }
        File.WriteAllText(Path.Combine(destDir, ".complete"), DateTimeOffset.UtcNow.ToString("O"));
    }

    private async Task DownloadFileAsync(string repoId, RemoteFile f, string destDir, Action<long> progress, CancellationToken ct)
    {
        var dest = Path.Combine(destDir, f.Path);
        if (File.Exists(dest) && new FileInfo(dest).Length == f.Size) { progress(f.Size); return; }
        var part = dest + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > f.Size) { File.Delete(part); have = 0; }

        if (have < f.Size)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{repoId}/resolve/main/{f.Path}");
            if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.OK && have > 0) have = 0; // server bỏ qua Range: tải lại từ đầu
            else if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Tải {f.Path} lỗi ({(int)resp.StatusCode}).");
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buf = new byte[1 << 20];
            var done = have;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                progress(done);
            }
        }

        if (new FileInfo(part).Length != f.Size) throw new IOException($"{f.Path}: tải thiếu, bấm tải lại để tiếp tục.");
        if (f.Sha256 is not null)
        {
            await using var fs = File.OpenRead(part);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (!hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(part);
                throw new IOException($"{f.Path}: sai SHA-256, đã xóa file tạm; bấm tải lại.");
            }
        }
        File.Move(part, dest, overwrite: true);
    }

    /// <summary>Tìm model NLLB đã tải theo cách cũ trong cache Hugging Face, trả về thư mục snapshot.</summary>
    public static string? FindInHfCache(string repoId)
    {
        var hub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "hub",
            "models--" + repoId.Replace("/", "--"), "snapshots");
        if (!Directory.Exists(hub)) return null;
        return Directory.EnumerateDirectories(hub).FirstOrDefault(d => File.Exists(Path.Combine(d, "config.json")));
    }
}
