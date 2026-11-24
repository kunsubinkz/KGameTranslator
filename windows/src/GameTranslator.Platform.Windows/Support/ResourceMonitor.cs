using System.Diagnostics;

namespace GameTranslator.Platform.Windows;

/// <summary>CPU của tiến trình app (% của toàn máy).</summary>
public sealed class ResourceMonitor
{
    private TimeSpan _lastCpu;
    private long _lastTicks;

    public ResourceMonitor()
    {
        _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
        _lastTicks = Stopwatch.GetTimestamp();
    }

    public double SampleCpuPercent()
    {
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds;
        double used = (cpu - _lastCpu).TotalSeconds;
        _lastCpu = cpu;
        _lastTicks = now;
        return elapsed <= 0 ? 0 : Math.Clamp(used / elapsed / Environment.ProcessorCount * 100, 0, 100);
    }

}
