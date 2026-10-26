using System.Runtime.Versioning;
using ChatApp.Media.Capture.Windows;

namespace ChatApp.Media.Tests;

/// <summary>
/// The graph string is the whole interface to the capture: a wrong option name or a
/// missing filter fails when someone starts sharing, not when the project builds. These
/// pin the shape that was verified against the filter's own option table.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsGraphicsCaptureTests
{
    private static string Graph(int frameRate = 60, int width = 0, int height = 0, int monitorWidth = 2560)
        => WindowsGraphicsCapture.BuildGraph(
            monitor: 65537, frameRate, width, height, monitorWidth);

    [WindowsFact]
    public void A_display_is_named_by_its_handle_rather_than_an_index()
    {
        // ddagrab takes a DXGI output index, which is only the same as the ordinal the
        // picker shows on a single-adapter machine — the mismatch shows the wrong screen.
        // A handle cannot be ambiguous.
        var graph = Graph();

        Assert.StartsWith("gfxcapture=hmonitor=65537:", graph);
        Assert.DoesNotContain("monitor_idx", graph);
    }

    [WindowsFact]
    public void The_frame_rate_is_a_ceiling_not_a_demand()
    {
        // WGC sends a frame when the screen changes, so the rate is the most it will
        // deliver rather than what it produces regardless.
        Assert.Contains("max_framerate=30", Graph(frameRate: 30));
    }

    [WindowsFact]
    public void Frames_are_brought_back_to_system_memory()
    {
        // gfxcapture hands over a D3D11 surface. Without the download the encoder gets a
        // frame it cannot read.
        Assert.EndsWith(",hwdownload,format=bgra", Graph());
    }

    [WindowsFact]
    public void No_size_asked_for_means_no_scaling_in_the_graph()
    {
        var graph = Graph(width: 0, height: 0);

        Assert.DoesNotContain("width=", graph);
        Assert.DoesNotContain("resize_mode", graph);
        Assert.DoesNotContain("scale_mode", graph);
    }

    [WindowsFact]
    public void A_requested_size_is_scaled_on_the_gpu_before_the_download()
    {
        // This is the win over ddagrab, where scale_d3d11 could not create its texture and
        // the CUDA map reported "function not implemented", so every frame came back at
        // full resolution and was scaled on the CPU. Sharing 1440p at 1080p now moves 42%
        // fewer bytes across the bus.
        var graph = Graph(width: 1920, height: 1080, monitorWidth: 2560);

        Assert.Contains(":width=1920:height=1080", graph);
        Assert.Contains("resize_mode=scale_aspect", graph);

        // The scale must come before the download, or it is not a GPU scale at all.
        Assert.True(
            graph.IndexOf("width=1920", StringComparison.Ordinal)
                < graph.IndexOf("hwdownload", StringComparison.Ordinal),
            "the scale was placed after the frame had already been downloaded");
    }

    [WindowsFact]
    public void Shrinking_past_half_size_uses_a_wider_kernel()
    {
        // Bilinear reads 2x2, so below half scale it skips pixels outright and text
        // shimmers as it scrolls — the same reason the ddagrab path asked swscale for area
        // averaging.
        Assert.Contains("scale_mode=bicubic", Graph(width: 1280, height: 720, monitorWidth: 2560));
        Assert.Contains("scale_mode=bilinear", Graph(width: 1920, height: 1080, monitorWidth: 2560));
    }
}
