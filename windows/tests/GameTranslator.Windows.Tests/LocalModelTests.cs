using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GameTranslator.Platform.Windows;
using GameTranslator.Platform.Windows.LocalModel;
using Xunit;

namespace GameTranslator.Windows.Tests;

public class JobObjectTests
{
    [Fact]
    public void ClosingJobKillsChild()
    {
        using var p = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        var job = new JobObject();
        job.Assign(p);
        Assert.False(p.HasExited);
        job.Dispose();
        Assert.True(p.WaitForExit(5000), "tiến trình con phải bị giết khi đóng Job Object");
    }
}

public class LocalHostManagerTests
{
    [Fact]
    public void FindFreePortFallsBackWhenBusy()
    {
        var busy = new TcpListener(System.Net.IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var port = ((IPEndPoint)busy.LocalEndpoint).Port;
            var got = LocalHostManager.FindFreePort(port);
            Assert.NotEqual(port, got);
            Assert.InRange(got, 1, 65535);
        }
        finally { busy.Stop(); }
    }

    [Theory]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", false, "xpu")]
    [InlineData("NVIDIA GeForce RTX 4070", false, "cuda")]
    [InlineData("AMD Radeon", false, "cpu")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", true, "cpu")]
    public void SuggestsBackend(string gpu, bool arm64, string expected) =>
        Assert.Equal(expected, PythonEnvironment.SuggestBackend([gpu], arm64));
}

public sealed class FakeHub : IDisposable
{
    private readonly HttpListener _l = new();
    public readonly List<string> Ranges = [];
    public readonly Dictionary<string, byte[]> Files = [];
    public bool CorruptHash;
    public string BaseUrl { get; }

    public FakeHub()
    {
        var port = ((IPEndPoint)Probe()).Port;
        BaseUrl = $"http://127.0.0.1:{port}";
        _l.Prefixes.Add(BaseUrl + "/");
        _l.Start();
        _ = Task.Run(Loop);
    }

    private static EndPoint Probe()
    {
        var t = new TcpListener(IPAddress.Loopback, 0);
        t.Start();
        var ep = t.LocalEndpoint;
        t.Stop();
        return ep;
    }

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch (Exception) { return; }
            var path = c.Request.Url!.AbsolutePath;
            if (path.EndsWith("/tree/main"))
            {
                var items = Files.Select(f =>
                {
                    var oid = CorruptHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(f.Value)).ToLowerInvariant();
                    return $"{{\"type\":\"file\",\"path\":\"{f.Key}\",\"size\":{f.Value.Length},\"lfs\":{{\"oid\":\"{oid}\",\"size\":{f.Value.Length}}}}}";
                });
                var json = Encoding.UTF8.GetBytes("[" + string.Join(",", items) + ",{\"type\":\"file\",\"path\":\"README.md\",\"size\":3},{\"type\":\"file\",\"path\":\"pytorch_model.bin\",\"size\":9}]");
                await c.Response.OutputStream.WriteAsync(json);
            }
            else
            {
                var name = path[(path.LastIndexOf('/') + 1)..];
                var data = Files[name];
                var range = c.Request.Headers["Range"];
                var start = 0;
                if (range is not null)
                {
                    Ranges.Add(range);
                    start = int.Parse(range.Replace("bytes=", "").TrimEnd('-'));
                    c.Response.StatusCode = 206;
                }
                await c.Response.OutputStream.WriteAsync(data.AsMemory(start));
            }
            c.Response.Close();
        }
    }

    public void Dispose() => _l.Close();
}

public class ModelDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gt-dl-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    private static byte[] Bytes(int n, int seed) => [.. Enumerable.Range(0, n).Select(i => (byte)((i * 31 + seed) & 0xFF))];

    private static FakeHub Hub() => new()
    {
        Files = { ["config.json"] = Bytes(100, 1), ["model.safetensors"] = Bytes(5000, 2) },
    };

    [Fact]
    public async Task ResumesFromPartFileAndVerifiesHash()
    {
        using var hub = Hub();
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "model.safetensors.part"), hub.Files["model.safetensors"][..1000]);
        var d = new ModelDownloader(new HttpClient(), hub.BaseUrl);
        var last = 0L;
        await d.DownloadAsync("org/m", _dir, p => last = p.Done, CancellationToken.None);

        Assert.Equal(hub.Files["model.safetensors"], File.ReadAllBytes(Path.Combine(_dir, "model.safetensors")));
        Assert.Equal(["bytes=1000-"], hub.Ranges);
        Assert.Equal(5100, last);
        Assert.True(ModelDownloader.IsComplete(_dir));
        Assert.False(File.Exists(Path.Combine(_dir, "README.md")), "không tải file thừa");
        Assert.False(File.Exists(Path.Combine(_dir, "pytorch_model.bin")), "không tải trọng số thừa khi đã có safetensors");
    }

    [Fact]
    public async Task BadHashDeletesPartAndFails()
    {
        using var hub = Hub();
        hub.CorruptHash = true;
        var d = new ModelDownloader(new HttpClient(), hub.BaseUrl);
        await Assert.ThrowsAsync<IOException>(() => d.DownloadAsync("org/m", _dir, _ => { }, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_dir, "model.safetensors")));
        Assert.False(File.Exists(Path.Combine(_dir, "model.safetensors.part")));
        Assert.False(ModelDownloader.IsComplete(_dir));
    }
}
