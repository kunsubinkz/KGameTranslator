using NAudio.CoreAudioApi;
using DataFlow = NAudio.CoreAudioApi.DataFlow;
using Vortice.MediaFoundation;

namespace GameTranslator.Platform.Windows.Capture;

public sealed record CaptureDevice(string Id, string Name);

public static class DeviceDiscovery
{
    private static bool _mfStarted;

    internal static void EnsureMediaFoundation()
    {
        if (_mfStarted) return;
        MediaFactory.MFStartup();
        _mfStarted = true;
    }

    /// <summary>Camera / capture card UVC (Media Foundation). `Id` là symbolic link của thiết bị.</summary>
    public static IReadOnlyList<CaptureDevice> VideoDevices()
    {
        try
        {
            EnsureMediaFoundation();
            using var attrs = MediaFactory.MFCreateAttributes(1);
            attrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
            var activates = MediaFactory.MFEnumDeviceSources(attrs);
            var list = new List<CaptureDevice>();
            foreach (var a in activates)
            {
                try
                {
                    list.Add(new CaptureDevice(
                        a.GetAllocatedString(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink) ?? "",
                        a.GetAllocatedString(CaptureDeviceAttributeKeys.FriendlyName) ?? "Camera"));
                }
                finally { a.Dispose(); }
            }
            return list;
        }
        catch (Exception) { return []; }
    }

    /// <summary>Capture card thường hiện như một micro/line-in; liệt kê mọi thiết bị thu để người dùng chọn.</summary>
    public static IReadOnlyList<CaptureDevice> AudioInputs() => Audio(DataFlow.Capture);

    /// <summary>Thiết bị phát tiếng (loa, tai nghe).</summary>
    public static IReadOnlyList<CaptureDevice> AudioOutputs() => Audio(DataFlow.Render);

    private static IReadOnlyList<CaptureDevice> Audio(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
                .Select(d => new CaptureDevice(d.ID, d.FriendlyName)).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>Ghép thiết bị tiếng theo tên card hình (như `bestAudioMatch` của bản macOS).</summary>
    public static CaptureDevice? BestAudioMatch(CaptureDevice? video, IReadOnlyList<CaptureDevice> audio)
    {
        if (video is null) return audio.FirstOrDefault();
        var words = SplitWords(video.Name);
        var best = audio
            .Select(d => (Device: d, Score: words.Count(w => w.Length > 2 && d.Name.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Score).FirstOrDefault();
        return best.Score > 0 ? best.Device : null;
    }

    internal static List<string> SplitWords(string name) =>
        System.Text.RegularExpressions.Regex.Split(name.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(s => s.Length > 0).ToList();
}
