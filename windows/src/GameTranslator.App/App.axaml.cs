using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace GameTranslator.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var session = new Session();
            var args = desktop.Args ?? [];
            if (args.Contains("--demo"))
            {
                // Chế độ thử: nguồn hình giả, bật dịch ngay (không cần capture card).
                session.Settings.VideoDeviceId = Platform.Windows.Capture.TestPatternSource.DeviceId;
                session.Settings.VideoDeviceName = Platform.Windows.Capture.TestPatternSource.DeviceName;
                session.SetTranslate(true);
            }
            desktop.MainWindow = new MainWindow(session);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
