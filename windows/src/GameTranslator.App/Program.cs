using Avalonia;

namespace GameTranslator.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run();
        if (args.Contains("--nllb-selftest")) return NllbSelfTest.Run();
        if (args.Contains("--probe-capture")) return CaptureProbe.Run();
        if (args.Contains("--ocr-image")) return OcrImageProbe.Run(args);
        if (args.Contains("--probe-live")) return LiveProbe.Run();
        if (args.Contains("--upscale-test")) return UpscaleProbe.Run(args);
        if (args.Contains("--upscale-perf")) return UpscaleProbe.Perf();
        var renderIdx = Array.IndexOf(args, "--render-ui");
        if (renderIdx >= 0) return UiRender.Run(args.ElementAtOrDefault(renderIdx + 1) ?? "ui-out");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
