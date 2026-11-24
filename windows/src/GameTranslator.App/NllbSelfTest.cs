using GameTranslator.Core;
using GameTranslator.Core.LocalHost;
using GameTranslator.Platform.Windows.LocalModel;

namespace GameTranslator.App;

/// <summary>`--nllb-selftest`: khởi động host (nếu đã cài), dịch 5 câu mẫu, in thiết bị và thời gian. Mã thoát khác 0 nếu hỏng.</summary>
internal static class NllbSelfTest
{
    private static readonly string[] Samples =
    [
        "Hello, traveler! Where are you headed?", "Be careful, the bridge is not safe anymore.", "Thank you for saving our village.",
        "Mr. Smith?! Wait... what is this place?", "Good luck out there, and stay safe.",
    ];

    public static int Run()
    {
        var settings = AppSettings.Load();
        var l = settings.Local;
        var paths = new NllbPaths(AppPaths.Nllb, AppPaths.NllbBundle, l.ModelsDir ?? Path.Combine(AppPaths.Nllb, "models"), l.ModelPaths);
        using var host = new LocalHostManager(paths, Path.Combine(AppPaths.Logs, "nllb-host.log"), () => l.ModelId) { Device = l.Device };
        if (!host.Environment.IsInstalled) { Console.WriteLine("Chưa cài môi trường Python. Mở app > Ctrl+Shift+M > Cài đặt dịch offline."); return 3; }
        if (!host.ModelDownloaded(l.ModelId)) { Console.WriteLine("Chưa tải model " + l.ModelId); return 3; }
        return RunAsync(host, l).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(LocalHostManager host, LocalTranslateSettings l)
    {
        await host.StartAsync();
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (host.Status.State != HostState.Ready)
        {
            if (host.Status.State == HostState.Error || DateTime.UtcNow > deadline)
            {
                Console.WriteLine("Host không sẵn sàng: " + host.Status.Text);
                foreach (var line in host.LogTail().TakeLast(10)) Console.WriteLine("  " + line);
                return 1;
            }
            await Task.Delay(500);
        }
        Console.WriteLine($"Host sẵn sàng: {host.Status.Model} · {host.Status.Device}");
        var tr = new LocalHostTranslator(() => host.BaseUrl, LocalHostManager.Token, () => l.Beams, timeoutSeconds: 30);
        var fail = 0;
        foreach (var s in Samples)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var r = await tr.TranslateAsync(new TranslationRequest(s));
                Console.WriteLine($"{s}\n  -> {r}  ({System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms)");
                if (r.Trim() == s.Trim()) fail++;
            }
            catch (TranslationException ex) { Console.WriteLine($"{s}\n  LỖI: {ex.Message}"); fail++; }
        }
        await host.StopAsync();
        Console.WriteLine(fail == 0 ? "OK" : $"{fail} câu lỗi hoặc không dịch");
        return fail == 0 ? 0 : 1;
    }
}
