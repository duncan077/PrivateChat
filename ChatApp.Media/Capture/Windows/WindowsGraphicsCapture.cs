using System.Globalization;
using System.Runtime.Versioning;
using ChatApp.Media.Codec;
using FFmpeg.AutoGen;

namespace ChatApp.Media.Capture.Windows;

/// <summary>
/// Builds capture graphs for <c>gfxcapture</c>, FFmpeg's Windows Graphics Capture source.
/// <para>
/// WGC is what Chromium's <c>getDisplayMedia</c> uses, and the reason a browser share
/// keeps working where this app's did not. Desktop Duplication — <c>ddagrab</c>, the
/// previous backend — stops delivering when a fullscreen application takes the display,
/// on a desktop switch, and across a UAC prompt, and reports nothing when it does.
/// </para>
/// <para>
/// It is also better on its own terms: it identifies a display by <c>HMONITOR</c> rather
/// than by a DXGI output index that has to be guessed at, and it scales on the GPU before
/// the frame is downloaded, which shrinks both the transfer and the colour conversion that
/// follows. Sharing a 1440p display at 1080p moves 42% fewer bytes across the bus than
/// downloading it whole and scaling on the CPU.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WindowsGraphicsCapture
{
    private static bool? _available;

    /// <summary>
    /// Whether this build of FFmpeg has the filter and this Windows can run it.
    /// <para>
    /// Monitor capture through WGC needs Windows 10 1903 (build 18362). Below that, and in
    /// FFmpeg builds predating the filter, the ddagrab path is all there is.
    /// </para>
    /// </summary>
    public static bool IsAvailable => _available ??= Probe();

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            return false;

        try
        {
            FFmpegLoader.Initialize();
            return ffmpeg.avfilter_get_by_name("gfxcapture") != null;
        }
        catch
        {
            // A missing or unloadable FFmpeg is reported properly elsewhere; here it just
            // means the filter cannot be used.
            return false;
        }
    }

    /// <summary>
    /// Builds the lavfi graph for a monitor.
    /// <para>
    /// <paramref name="width"/> and <paramref name="height"/> of zero capture the display
    /// at its own size.
    /// </para>
    /// </summary>
    public static string BuildGraph(nint monitor, int frameRate, int width, int height, int monitorWidth)
    {
        var graph =
            $"gfxcapture=hmonitor={((ulong)monitor).ToString(CultureInfo.InvariantCulture)}" +
            $":max_framerate={frameRate.ToString(CultureInfo.InvariantCulture)}";

        if (width > 0 && height > 0)
        {
            // scale_aspect never distorts: if the requested size does not match the
            // display's shape it fits inside rather than stretching. Callers already
            // preserve the aspect ratio, so this only guards against the case where they
            // stop.
            graph +=
                $":width={width.ToString(CultureInfo.InvariantCulture)}" +
                $":height={height.ToString(CultureInfo.InvariantCulture)}" +
                ":resize_mode=scale_aspect" +

                // Bilinear reads a 2x2 neighbourhood, so shrinking by more than half skips
                // pixels entirely and text shimmers as it scrolls. Bicubic reads 4x4 and
                // holds up to roughly a quarter scale, which covers every ladder step the
                // share quality guard uses.
                (width * 2 <= monitorWidth ? ":scale_mode=bicubic" : ":scale_mode=bilinear");
        }

        // gfxcapture hands over a D3D11 surface like ddagrab does, so the frame still has
        // to come back to system memory. The conversion to I420 happens once afterwards,
        // in the capture source's scaler.
        return graph + ",hwdownload,format=bgra";
    }
}
