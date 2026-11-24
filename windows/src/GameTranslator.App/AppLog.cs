namespace GameTranslator.App;

/// <summary>Ghi log ra %LOCALAPPDATA%\GameTranslator\logs\app.log (không bao giờ ghi API key).</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var path = Path.Combine(AppPaths.Logs, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { }
    }
}
