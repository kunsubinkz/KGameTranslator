using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Threading;
using GameTranslator.Platform.Windows.Capture;

namespace GameTranslator.App;

/// <summary>`--render-ui &lt;thư mục&gt;`: xuất ảnh PNG các cửa sổ (Avalonia.Headless, không cần màn hình) để so với bản thiết kế.</summary>
internal static class UiRender
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        using var session = new Session();
        var frame = TestPatternSource.RenderDialogFrame(TestPatternSource.Lines[0], 1280, 720);
        session.Cache.Store(session.Current.Id, "Welcome to Hyrule, traveler.", "Chào mừng đến Hyrule, lữ khách.", "claude:claude-haiku-5-5");
        session.Cache.Store(session.Current.Id, "Take this sword.", "Hãy cầm lấy thanh kiếm này.", "claude:claude-haiku-5-5");

        void Shot(string name, Window w, int tab = -1)
        {
            w.Show();
            if (tab >= 0 && w.Content is TabControl tc) tc.SelectedIndex = tab;
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
            w.Close();
        }

        for (int i = 0; i < 5; i++)
            Shot($"cai-dat-{i + 1}", new SettingsWindow(session, () => { }) { Width = 760, Height = 760 }, i);
        Shot("model-local", new LocalModelWindow(session) { Width = 860, Height = 800 });
        // Trạng thái "đã cài" dựng từ thư mục giả trong %TEMP% (không đụng dữ liệu thật).
        var fake = Path.Combine(Path.GetTempPath(), "gt-nllb-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fake, "venv", "Scripts"));
        File.WriteAllText(Path.Combine(fake, "venv", "Scripts", "python.exe"), "");
        File.WriteAllText(Path.Combine(fake, "env.json"), "{\"Backend\":\"xpu\",\"Torch\":\"2.9.1+xpu\",\"Transformers\":\"4.57.1\",\"Python\":\"3.12.8\",\"Xpu\":true,\"Cuda\":false,\"DeviceName\":\"Intel(R) Arc(TM) B580 Graphics\"}");
        var m600 = Path.Combine(fake, "models", "facebook--nllb-200-distilled-600M");
        Directory.CreateDirectory(m600);
        File.WriteAllText(Path.Combine(m600, "config.json"), "{}");
        File.WriteAllText(Path.Combine(m600, "model.safetensors"), "x");
        AppPaths.NllbOverride = fake;
        session.ReconfigureLocalHost();
        Shot("model-local-da-cai", new LocalModelWindow(session) { Width = 860, Height = 1250 });
        AppPaths.NllbOverride = null;
        try { Directory.Delete(fake, true); } catch (Exception) { }
        Shot("phu-de-nhanh", new SubtitleQuickWindow(session));
        Shot("lich-su", new HistoryWindow(session));
        Shot("ho-so-game", new ProfilesWindow(session, () => { }, () => { }));
        Shot("chon-vung", new RegionSelectorWindow(session, (frame, 1280, 720)) { Width = 1100, Height = 780 });
        // Cửa sổ chính cuối cùng: đóng nó sẽ giải phóng Session.
        Shot("cua-so-chinh", new MainWindow(session) { Width = 1280, Height = 400 });
        Console.WriteLine("Đã xuất ảnh vào " + Path.GetFullPath(dir));
        return 0;
    }
}
