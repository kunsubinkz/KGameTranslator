using System.Diagnostics;
using System.Text;

namespace GameTranslator.Platform.Windows.LocalModel;

/// <summary>Chạy một tiến trình, đọc từng dòng stdout/stderr; hủy thì giết cả cây tiến trình.</summary>
public static class ProcessRunner
{
    public static async Task<int> RunAsync(string exe, IEnumerable<string> args, Action<string>? onLine,
        CancellationToken ct, IDictionary<string, string>? env = null, string? workDir = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workDir is not null) psi.WorkingDirectory = workDir;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (Exception) { } });
        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }
}
