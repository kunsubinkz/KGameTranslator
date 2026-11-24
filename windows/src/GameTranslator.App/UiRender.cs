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
