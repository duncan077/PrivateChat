using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using ChatApp.Core.Abstractions;
using ChatApp.Core.Media;
using ChatApp.Core.State;
using ChatApp.Media.Capture.Windows;
using ChatApp.Media.Codec;
using ChatApp.Media.Engine;
using ChatApp.Media.Janus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatApp.Desktop;

/// <summary>
/// Registers the desktop platform implementations behind the abstractions
/// <c>ChatApp.Core</c> declares.
/// </summary>
internal static class WindowsPlatformServices
{
    public static void Register(IServiceCollection services)
    {
        // WinExe has no console, so the file log is the only place a failed call leaves
        // evidence. Debug level: connection setup is exactly what needs diagnosing.
        services.AddLogging(builder => builder
            .AddProvider(new FileLoggerProvider())
            .SetMinimumLevel(LogLevel.Debug));

        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IGpuInfoProvider, WindowsGpuInfoProvider>();
            services.AddSingleton<ICaptureBackendFactory>(sp =>
                new WindowsCaptureBackendFactory(sp.GetService<ILoggerFactory>()?.CreateLogger("Capture")));
        }
        else
        {
            // Linux capture backends are not implemented yet; the app still runs for
            // chat, and starting a call surfaces a clear error rather than crashing.
            services.AddSingleton<IGpuInfoProvider, NullGpuInfoProvider>();
        }

        services.AddSingleton<IMediaEngine>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();

            // SIPSorcery owns ICE, DTLS and SRTP. Without its logging, a transport that
            // never connects reports nothing but a final "failed" state.
            if (loggerFactory is not null)
                JanusTransportLogging.Enable(loggerFactory);

            return new NativeMediaEngine(
                sp.GetRequiredService<ICaptureBackendFactory>(),
                sp.GetRequiredService<CallStore>().BuildVideoEncoderPolicy(),
                loggerFactory?.CreateLogger("MediaEngine"));
        });

        // Scales and converts decoded frames for the renderer. Registered even without a
        // capture backend, because inbound video still needs drawing on Linux.
        services.AddSingleton<IVideoFrameConverterFactory, SwsVideoFrameConverterFactory>();

        services.AddSingleton<MediaEngineHost>();
    }
}

/// <summary>
/// Adapter graphics enumeration via WMI, replacing the inline <c>#if WINDOWS</c> query
/// that used to sit inside <c>CallStore</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsGpuInfoProvider : IGpuInfoProvider
{
    private IReadOnlyList<string>? _cached;

    public IReadOnlyList<string> GetHardwareGpuNames()
    {
        if (_cached is { } cached)
            return cached;

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            using var controllers = searcher.Get();

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var controller in controllers)
            {
                var raw = controller["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var name = raw.Trim();

                // The Basic Render Driver is a software fallback, not a real adapter.
                if (name.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (seen.Add(name))
                    names.Add(name);
            }

            _cached = names;
            return names;
        }
        catch
        {
            // WMI can be disabled by policy; an empty list just means no GPU preference UI.
            _cached = [];
            return _cached;
        }
    }

    public bool HasMultipleHardwareGpus => GetHardwareGpuNames().Count > 1;
}
