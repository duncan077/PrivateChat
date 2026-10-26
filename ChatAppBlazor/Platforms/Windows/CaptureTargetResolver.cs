#if WINDOWS
using System.Text;
using ChatAppBlazor.Services;
using Shared.Models;

namespace ChatAppBlazor.Platforms.Windows;

/// <summary>
/// Maps the opaque capture identifier reported by <c>getDisplayMedia</c> back to the
/// monitor or window it refers to, so the annotation overlay can be positioned over
/// exactly the region being published.
///
/// Chromium (and therefore WebView2) exposes labels shaped like
/// <c>screen:&lt;index&gt;:&lt;n&gt;</c> and <c>window:&lt;hwnd&gt;:&lt;n&gt;</c>.
/// Neither is contractual, so every lookup falls back to geometry matching.
/// </summary>
internal static class CaptureTargetResolver
{
    internal readonly record struct Resolution(
        ScreenAnnotationCaptureKind Kind,
        NativeMethods.RECT Bounds,
        IntPtr WindowHandle);

    private const float AspectTolerance = 0.02f;

    public static Resolution Resolve(ScreenAnnotationCaptureTarget target)
    {
        var label = (target.TargetLabel ?? string.Empty).Trim();
        var surface = (target.DisplaySurface ?? string.Empty).Trim().ToLowerInvariant();

        if (LooksLikeWindowCapture(label, surface))
        {
            if (TryResolveWindow(label, out var hwnd, out var windowBounds))
            {
                return new Resolution(ScreenAnnotationCaptureKind.Window, windowBounds, hwnd);
            }

            return new Resolution(ScreenAnnotationCaptureKind.Unknown, default, IntPtr.Zero);
        }

        if (LooksLikeMonitorCapture(label, surface))
        {
            if (TryResolveMonitor(label, target.FrameWidth, target.FrameHeight, out var monitorBounds))
            {
                return new Resolution(ScreenAnnotationCaptureKind.Monitor, monitorBounds, IntPtr.Zero);
            }
        }

        return new Resolution(ScreenAnnotationCaptureKind.Unknown, default, IntPtr.Zero);
    }

    private static bool LooksLikeWindowCapture(string label, string surface)
    {
        if (label.StartsWith("window:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (surface is "window" or "application")
        {
            return true;
        }

        // A shared browser tab has no desktop window of its own.
        return false;
    }

    private static bool LooksLikeMonitorCapture(string label, string surface)
        => surface is "monitor" or "screen"
           || label.StartsWith("screen:", StringComparison.OrdinalIgnoreCase)
           || label.StartsWith("monitor:", StringComparison.OrdinalIgnoreCase)
           || label.Contains("entire screen", StringComparison.OrdinalIgnoreCase);

    // ─── Windows ───────────────────────────────────────────────────────────────

    private static bool TryResolveWindow(string label, out IntPtr hwnd, out NativeMethods.RECT bounds)
    {
        hwnd = IntPtr.Zero;
        bounds = default;

        if (!TryParseIdToken(label, "window:", out var rawId))
        {
            return false;
        }

        foreach (var candidate in EnumerateHandleCandidates(rawId))
        {
            if (IsUsableWindow(candidate) && TryGetWindowBounds(candidate, out bounds))
            {
                hwnd = candidate;
                return true;
            }
        }

        // Some builds only report the low 32 bits of the handle; scan for a match.
        var targetLow32 = (uint)(rawId & 0xFFFF_FFFF);
        IntPtr match = IntPtr.Zero;

        NativeMethods.EnumWindows((candidate, _) =>
        {
            var value = unchecked((ulong)(long)candidate);
            if ((uint)(value & 0xFFFF_FFFF) != targetLow32 || !IsUsableWindow(candidate))
            {
                return true;
            }

            match = candidate;
            return false;
        }, IntPtr.Zero);

        if (match == IntPtr.Zero || !TryGetWindowBounds(match, out bounds))
        {
            return false;
        }

        hwnd = match;
        return true;
    }

    private static IEnumerable<IntPtr> EnumerateHandleCandidates(ulong rawId)
    {
        yield return new IntPtr(unchecked((long)rawId));
        yield return new IntPtr(unchecked((int)(rawId & 0xFFFF_FFFF)));
        yield return new IntPtr(unchecked((int)(uint)(rawId & 0xFFFF_FFFF)));
    }

    private static bool IsUsableWindow(IntPtr hwnd)
        => hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd);

    private static bool TryGetWindowBounds(IntPtr hwnd, out NativeMethods.RECT bounds)
    {
        bounds = default;

        var size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>();
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frameBounds,
                size) == 0 &&
            frameBounds.Width > 0 &&
            frameBounds.Height > 0)
        {
            bounds = frameBounds;
            return true;
        }

        return NativeMethods.GetWindowRect(hwnd, out bounds) && bounds.Width > 0 && bounds.Height > 0;
    }

    // ─── Monitors ──────────────────────────────────────────────────────────────

    private static bool TryResolveMonitor(string label, int frameWidth, int frameHeight, out NativeMethods.RECT bounds)
    {
        bounds = default;

        var monitors = EnumerateMonitors();
        if (monitors.Count == 0)
        {
            return false;
        }

        // 1. Chromium usually encodes the monitor's enumeration index in the label.
        if (TryParseIdToken(label, "screen:", out var rawIndex) &&
            rawIndex < (ulong)monitors.Count &&
            IsPlausibleMatch(monitors[(int)rawIndex].Bounds, frameWidth, frameHeight))
        {
            bounds = monitors[(int)rawIndex].Bounds;
            return true;
        }

        // 2. Exact pixel match wins when it is unambiguous.
        var exact = monitors
            .Where(monitor => monitor.Bounds.Width == frameWidth && monitor.Bounds.Height == frameHeight)
            .ToList();
        if (exact.Count == 1)
        {
            bounds = exact[0].Bounds;
            return true;
        }

        // 3. The publisher scales frames down, so fall back to aspect ratio.
        if (frameWidth > 0 && frameHeight > 0)
        {
            var frameAspect = (float)frameWidth / frameHeight;
            var aspectMatches = monitors
                .Where(monitor => MathF.Abs(((float)monitor.Bounds.Width / monitor.Bounds.Height) - frameAspect) <= AspectTolerance)
                .ToList();

            if (aspectMatches.Count == 1)
            {
                bounds = aspectMatches[0].Bounds;
                return true;
            }

            if (aspectMatches.Count > 1)
            {
                var primaryMatch = aspectMatches.FirstOrDefault(monitor => monitor.IsPrimary);
                bounds = primaryMatch.Bounds.Width > 0 ? primaryMatch.Bounds : aspectMatches[0].Bounds;
                return true;
            }
        }

        // 4. Last resort: the primary display.
        var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary);
        bounds = primary.Bounds.Width > 0 ? primary.Bounds : monitors[0].Bounds;
        return true;
    }

    private static bool IsPlausibleMatch(NativeMethods.RECT bounds, int frameWidth, int frameHeight)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return true;
        }

        if (bounds.Width == frameWidth && bounds.Height == frameHeight)
        {
            return true;
        }

        var monitorAspect = (float)bounds.Width / bounds.Height;
        var frameAspect = (float)frameWidth / frameHeight;
        return MathF.Abs(monitorAspect - frameAspect) <= AspectTolerance;
    }

    private readonly record struct MonitorInfo(NativeMethods.RECT Bounds, bool IsPrimary, string DeviceName);

    private static List<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();

        bool CollectMonitor(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData)
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                szDevice = string.Empty
            };

            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new MonitorInfo(
                    info.rcMonitor,
                    (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    info.szDevice ?? string.Empty));
            }

            return true;
        }

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, CollectMonitor, IntPtr.Zero);

        // EnumDisplayMonitors order is not guaranteed to match the display device
        // index Chromium uses, so sort by device name (\\.\DISPLAY1, DISPLAY2, ...).
        monitors.Sort(static (left, right) =>
            string.Compare(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase));

        return monitors;
    }

    private static bool TryParseIdToken(string label, string prefix, out ulong value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(label) || !label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tokens = label.Split(':', StringSplitOptions.TrimEntries);
        return tokens.Length >= 2 && ulong.TryParse(tokens[1], out value);
    }

    /// <summary>Window title, used only for diagnostics.</summary>
    public static string? GetWindowTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return null;
        }

        var builder = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
        var title = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }
}
#endif
