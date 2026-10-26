using System.Globalization;
using System.Runtime.Versioning;
using ChatApp.Core.Media;
using ChatApp.Media.Capture.Windows;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Capture;

/// <summary>
/// Screen capture through FFmpeg.
/// <para>
/// Windows uses <c>gfxcapture</c>, which is Windows Graphics Capture exposed as a filter
/// and hosted by the <c>lavfi</c> input device — the same API Chromium's
/// <c>getDisplayMedia</c> uses. It replaces <c>ddagrab</c> (Desktop Duplication), which
/// stops delivering frames when a fullscreen application takes the display, on a desktop
/// switch, and across a UAC prompt, and reports no error when it does: the share simply
/// freezes while every statistic on both ends reads healthy. <c>ddagrab</c> stays as the
/// fallback for builds and Windows versions without WGC. Linux uses <c>x11grab</c>.
/// </para>
/// <para>
/// Both produce D3D11 surfaces, so the graph appends <c>hwdownload</c> and
/// <c>format=bgra</c> to bring frames back to system memory. A future zero-copy path
/// could hand those surfaces straight to a hardware encoder and skip the download.
/// </para>
/// <para>
/// The two differ in one way that matters upstream: WGC delivers a frame only when the
/// screen changes, where Desktop Duplication repeats the last one to hold the frame rate.
/// A still screen is therefore silent rather than idling at the full rate — cheaper, but
/// it means silence no longer implies a dead capture. <see cref="StallTimeout"/> is how
/// that is communicated to the engine's watchdog.
/// </para>
/// </summary>
public sealed class FFmpegScreenCapture : IScreenCapture
{
    private readonly ILogger _logger;
    private readonly FFmpegCaptureSource _source;

    public bool IsCapturing => _source.IsCapturing;
    public int Width => _source.Width;
    public int Height => _source.Height;

    public event VideoCapturedHandler? FrameCaptured
    {
        add => _source.FrameCaptured += value;
        remove => _source.FrameCaptured -= value;
    }

    public event Action<string>? CaptureFailed
    {
        add => _source.CaptureFailed += value;
        remove => _source.CaptureFailed -= value;
    }

    public FFmpegScreenCapture(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _source = new FFmpegCaptureSource(_logger);
    }

    /// <summary>
    /// Lists shareable monitors.
    /// <para>
    /// Window and per-application capture are not offered here: <c>ddagrab</c> duplicates
    /// whole outputs only. Those targets need a separate Windows Graphics Capture
    /// implementation.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<ScreenShareTarget>> GetTargetsAsync(CancellationToken cancellationToken = default)
    {
        var targets = new List<ScreenShareTarget>();

        if (OperatingSystem.IsWindows())
        {
            foreach (var monitor in Windows.WindowsMonitorEnumerator.GetMonitors())
            {
                // The id is ddagrab's output_idx; the ordinal-based mapping is explained
                // on the enumerator.
                targets.Add(new ScreenShareTarget(
                    Id: monitor.OutputIndex.ToString(CultureInfo.InvariantCulture),
                    Title: monitor.IsPrimary
                        ? $"Display {monitor.OutputIndex + 1} (primary)"
                        : $"Display {monitor.OutputIndex + 1}",
                    Kind: ScreenShareTargetKind.Monitor,
                    Width: monitor.Width,
                    Height: monitor.Height));
            }

            if (targets.Count == 0)
            {
                _logger.LogWarning("EnumDisplayMonitors returned nothing; offering output 0 blind.");
                targets.Add(new ScreenShareTarget("0", "Primary display", ScreenShareTargetKind.Monitor, 0, 0));
            }
        }
        else
        {
            targets.Add(new ScreenShareTarget("0", "Screen", ScreenShareTargetKind.Monitor, 0, 0));
        }

        return Task.FromResult<IReadOnlyList<ScreenShareTarget>>(targets);
    }

    /// <summary>
    /// How long this capture may legitimately produce nothing, or <see langword="null"/>
    /// when silence carries no information.
    /// <para>
    /// Desktop Duplication repeats the last frame to hold the frame rate, so it is never
    /// legitimately silent and two seconds of nothing means it has died — the failure it
    /// never reports. Windows Graphics Capture sends a frame only when the screen changes,
    /// so a user reading a still document produces nothing for minutes at a time and no
    /// amount of silence proves anything. Its real failures — the display going away, the
    /// session closing — arrive through <see cref="CaptureFailed"/> instead.
    /// </para>
    /// </summary>
    public TimeSpan? StallTimeout { get; private set; } = TimeSpan.FromSeconds(2);

    public Task StartAsync(
        ScreenShareTarget target,
        int frameRate,
        int width = 0,
        int height = 0,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var monitor = ResolveMonitor(target);
            var scaled = width > 0 && height > 0;

            if (WindowsGraphicsCapture.IsAvailable && monitor is { Handle: not 0 })
            {
                _source.Start(
                    "lavfi",
                    WindowsGraphicsCapture.BuildGraph(monitor.Handle, frameRate, width, height, monitor.Width),
                    new Dictionary<string, string>());

                StallTimeout = null;

                _logger.LogInformation(
                    "Screen capture started on {Device} ({Width}x{Height}) at {Fps}fps via Windows Graphics " +
                    "Capture{Scaled}.",
                    monitor.DeviceName, monitor.Width, monitor.Height, frameRate,
                    scaled ? $", scaled on the GPU to {width}x{height}" : string.Empty);

                return Task.CompletedTask;
            }

            var outputIndex = monitor?.OutputIndex ?? (int.TryParse(target.Id, out var parsed) ? parsed : 0);

            // ddagrab is a filter, so it is driven through the lavfi input device.
            // hwdownload + format=bgra move the D3D11 surface into system memory.
            var graph =
                $"ddagrab=output_idx={outputIndex.ToString(CultureInfo.InvariantCulture)}" +
                $":framerate={frameRate.ToString(CultureInfo.InvariantCulture)}" +
                ",hwdownload,format=bgra";

            // Resizing while the frame is still a D3D11 surface would be better — it would
            // shrink the download too — but neither route works from ddagrab's output here:
            // scale_d3d11 fails to create its texture (E_INVALIDARG), and mapping the
            // surface into CUDA for scale_cuda reports "function not implemented". So the
            // scale happens after the download, but still inside the capture graph, where
            // it fuses with the pixel-format conversion into a single pass and hands the
            // encoder a frame that already matches its own size. Doing it later — in the
            // capture source and then again in the encoder — meant two passes over a
            // full-resolution picture. gfxcapture has no such trouble and scales on the
            // GPU, which is one more reason it is preferred.
            if (scaled)
            {
                var w = width.ToString(CultureInfo.InvariantCulture);
                var h = height.ToString(CultureInfo.InvariantCulture);

                // Bilinear point-samples when shrinking, so a share at less than half size
                // drops every other pixel and text shimmers as it moves. Area averaging
                // reads all of them, and costs little at these sizes.
                var flags = width * 2 <= target.Width ? ":flags=area" : string.Empty;

                graph += $",scale={w}:{h}{flags},format=yuv420p";
            }

            _source.Start("lavfi", graph, new Dictionary<string, string>());

            StallTimeout = TimeSpan.FromSeconds(2);

            _logger.LogInformation(
                "Screen capture started on output {Index} at {Fps}fps via Desktop Duplication{Scaled}. " +
                "Windows Graphics Capture was unavailable, so a fullscreen application can silently " +
                "freeze this share.",
                outputIndex,
                frameRate,
                scaled ? $", captured at {width}x{height}" : string.Empty);

            return Task.CompletedTask;
        }

        // x11grab is an input device rather than a filter, and its video_size crops the
        // capture region instead of scaling it, so there is nowhere here to honour a
        // requested size. The encoder's own scaler handles it on the CPU until this moves
        // to PipeWire, where a scale filter can sit in the graph as it does on Windows.
        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0.0";
        _source.Start("x11grab", display, new Dictionary<string, string>
        {
            ["framerate"] = frameRate.ToString(CultureInfo.InvariantCulture),
            // Cursor is part of what people expect to share.
            ["draw_mouse"] = "1"
        });

        StallTimeout = TimeSpan.FromSeconds(2);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Finds the monitor a target names, freshly each time it is started.
    /// <para>
    /// The id is an ordinal rather than a handle because it is persisted and reused — the
    /// stall watchdog restarts a share with the target it was given minutes earlier — and
    /// <c>HMONITOR</c> values do not survive a display being reconfigured. Resolving at
    /// start means a share restarted after the monitor list changed either finds the right
    /// display or falls back cleanly.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private WindowsMonitorEnumerator.Monitor? ResolveMonitor(ScreenShareTarget target)
    {
        if (!int.TryParse(target.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return null;

        try
        {
            return WindowsMonitorEnumerator.GetMonitors().FirstOrDefault(m => m.OutputIndex == index);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate monitors; falling back to the target's index.");
            return null;
        }
    }

    public Task StopAsync()
    {
        _source.Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => _source.Dispose();
}
