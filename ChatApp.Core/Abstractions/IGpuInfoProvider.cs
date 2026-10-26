namespace ChatApp.Core.Abstractions;

/// <summary>
/// Reports the machine's graphics adapters so the settings dialog can offer a
/// GPU preference and the encoder can pick a hardware path.
/// <para>
/// The Blazor client queried WMI (<c>Win32_VideoController</c>) inline inside
/// <c>CallStore</c> behind a <c>#if WINDOWS</c>. That is Windows-only, so it now
/// lives behind this interface with a no-op default for platforms that cannot
/// enumerate adapters.
/// </para>
/// </summary>
public interface IGpuInfoProvider
{
    /// <summary>Hardware adapters, excluding software renderers. May be empty.</summary>
    IReadOnlyList<string> GetHardwareGpuNames();

    bool HasMultipleHardwareGpus { get; }
}

/// <summary>Fallback used on platforms with no adapter enumeration.</summary>
public sealed class NullGpuInfoProvider : IGpuInfoProvider
{
    public IReadOnlyList<string> GetHardwareGpuNames() => [];

    public bool HasMultipleHardwareGpus => false;
}
