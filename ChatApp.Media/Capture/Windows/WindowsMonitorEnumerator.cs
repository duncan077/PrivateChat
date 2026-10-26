using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChatApp.Media.Capture.Windows;

/// <summary>
/// Real monitor enumeration via <c>EnumDisplayMonitors</c>, replacing the fabricated
/// "Primary/Secondary monitor" list that offered two entries regardless of how many
/// monitors exist and no geometry for any of them.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WindowsMonitorEnumerator
{
    internal sealed record Monitor(
        int OutputIndex,
        string DeviceName,
        int Width,
        int Height,
        bool IsPrimary,
        nint Handle);

    /// <summary>
    /// Lists monitors ordered by their GDI device ordinal (<c>\\.\DISPLAY1</c> → 0).
    /// <para>
    /// Each carries its <c>HMONITOR</c>, which is what Windows Graphics Capture identifies
    /// a display by. The ddagrab fallback instead wants a DXGI output index, and DXGI
    /// enumerates outputs per adapter in driver order — which happens to match the GDI
    /// ordinals on a single-adapter machine and can differ on multi-adapter setups. The
    /// handle has no such ambiguity, so that guess only applies when the fallback is in
    /// use.
    /// </para>
    /// </summary>
    public static List<Monitor> GetMonitors()
    {
        var collected = new List<(nint Handle, string Device, int Width, int Height, bool Primary)>();
        var handle = GCHandle.Alloc(collected);

        try
        {
            EnumDisplayMonitors(0, null, &EnumCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return collected
            .OrderBy(m => ParseDisplayOrdinal(m.Device))
            .Select((m, index) => new Monitor(index, m.Device, m.Width, m.Height, m.Primary, m.Handle))
            .ToList();
    }

    /// <summary>"\\.\DISPLAY12" → 12; unparsable names sort last, after the real ones.</summary>
    private static int ParseDisplayOrdinal(string deviceName)
    {
        var digits = deviceName.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray();
        return digits.Length > 0 && int.TryParse(digits, out var ordinal) ? ordinal : int.MaxValue;
    }

    [UnmanagedCallersOnly]
    private static int EnumCallback(nint monitor, nint hdc, Rect* rect, nint data)
    {
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };

        if (GetMonitorInfoW(monitor, &info))
        {
            var list = (List<(nint, string, int, int, bool)>)GCHandle.FromIntPtr(data).Target!;

            // Bounded read: szDevice is documented null-terminated within 32 chars, but
            // a full buffer must not send the string constructor past the struct.
            var device = new ReadOnlySpan<char>(info.Device, 32);
            var terminator = device.IndexOf('\0');
            if (terminator >= 0)
                device = device[..terminator];

            var name = device.ToString();

            // GetMonitorInfo reports logical, DPI-scaled coordinates unless the process is
            // per-monitor DPI aware: a 2560x1440 display at 125% comes back as 2048x1152.
            // That number reaches the picker and, worse, is the ceiling a requested share
            // resolution is capped against — so a user asking for 1440p on a 1440p screen
            // silently got 1152p. EnumDisplaySettings reports the real pixel mode whatever
            // the process is aware of, and matches what the capture actually produces.
            var mode = new DevMode { Size = (ushort)sizeof(DevMode) };
            var physical = EnumDisplaySettingsW(name, EnumCurrentSettings, &mode);

            list.Add((
                monitor,
                name,
                physical ? (int)mode.PelsWidth : info.MonitorRect.Right - info.MonitorRect.Left,
                physical ? (int)mode.PelsHeight : info.MonitorRect.Bottom - info.MonitorRect.Top,
                (info.Flags & MonitorInfoPrimary) != 0));
        }

        return 1; // continue enumeration
    }

    private const uint MonitorInfoPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect MonitorRect;
        public Rect WorkRect;
        public uint Flags;
        public fixed char Device[32];
    }

    [DllImport("user32.dll")]
    private static extern int EnumDisplayMonitors(
        nint hdc, Rect* clip, delegate* unmanaged<nint, nint, Rect*, nint, int> callback, nint data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(nint monitor, MonitorInfoEx* info);

    private const int EnumCurrentSettings = -1;

    /// <summary>
    /// Only <c>dmPelsWidth</c> and <c>dmPelsHeight</c> are read, but the whole of
    /// <c>DEVMODEW</c> has to be laid out: the driver fills the struct it is told the size
    /// of, and a short one is written past.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TrueTypeOption, Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType;
        public uint Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, DevMode* mode);
}
