using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ChatApp.Media.Codec;

/// <summary>Outcome of trying to bring up FFmpeg.</summary>
public sealed record FFmpegAvailability(
    bool IsAvailable,
    string? Version,
    string? SearchPath,
    string? Diagnostic);

/// <summary>
/// Initializes the FFmpeg bindings and reports precisely why they are unusable when
/// they are.
/// <para>
/// <c>FFmpeg.AutoGen</c> ships bindings only — no binaries. It needs the <b>shared</b>
/// FFmpeg libraries (<c>avcodec-62</c>, <c>avutil-60</c>, <c>swscale-9</c>, …) matching
/// FFmpeg 8.x. A static build such as the one <c>winget install Gyan.FFmpeg</c>
/// produces contains only <c>ffmpeg.exe</c> and will not work, which is a confusing
/// failure worth naming explicitly.
/// </para>
/// </summary>
public static class FFmpegLoader
{
    private static readonly Lock Gate = new();
    private static FFmpegAvailability? _cached;

    /// <summary>Directory holding the shared FFmpeg libraries. Set before first use.</summary>
    public static string? OverridePath { get; set; }

    /// <summary>
    /// Initializes FFmpeg once and caches the result. Never throws: callers decide
    /// whether a missing FFmpeg is fatal.
    /// </summary>
    public static FFmpegAvailability Initialize()
    {
        if (_cached is { } cached)
            return cached;

        lock (Gate)
        {
            _cached ??= InitializeCore();
            return _cached;
        }
    }

    private static FFmpegAvailability InitializeCore()
    {
        var searchPath = ResolveSearchPath();

        try
        {
            ffmpeg.RootPath = searchPath;

            // Required for the dynamic binding mode: without it the function resolver is
            // never installed and every call fails with EntryPointNotFoundException.
            DynamicallyLoadedBindings.Initialize();

            // Force an actual call so a missing or ABI-mismatched library surfaces here
            // rather than deep inside encoder setup.
            var version = ffmpeg.av_version_info();

            return new FFmpegAvailability(
                IsAvailable: true,
                Version: version,
                SearchPath: string.IsNullOrEmpty(searchPath) ? "(system search path)" : searchPath,
                Diagnostic: null);
        }
        catch (Exception ex)
        {
            return new FFmpegAvailability(
                IsAvailable: false,
                Version: null,
                SearchPath: string.IsNullOrEmpty(searchPath) ? "(system search path)" : searchPath,
                Diagnostic: BuildDiagnostic(searchPath, ex));
        }
    }

    private static string ResolveSearchPath()
    {
        if (!string.IsNullOrWhiteSpace(OverridePath))
            return OverridePath;

        // Android resolves natives from the APK's lib/<abi> directory automatically;
        // an empty root means "probe by name".
        if (OperatingSystem.IsAndroid())
            return string.Empty;

        var ridPath = Path.Combine(
            AppContext.BaseDirectory, "runtimes", CurrentRid(), "native");

        if (Directory.Exists(ridPath) && ContainsSharedFFmpeg(ridPath))
            return ridPath;

        var localPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Directory.Exists(localPath) && ContainsSharedFFmpeg(localPath))
            return localPath;

        if (ContainsSharedFFmpeg(AppContext.BaseDirectory))
            return AppContext.BaseDirectory;

        // Empty tells FFmpeg.AutoGen to fall back to the OS loader search path.
        return string.Empty;
    }

    private static bool ContainsSharedFFmpeg(string directory)
    {
        try
        {
            var pattern = OperatingSystem.IsWindows() ? "avcodec*.dll" : "libavcodec*";
            return Directory.EnumerateFiles(directory, pattern).Any();
        }
        catch
        {
            return false;
        }
    }

    private static string BuildDiagnostic(string searchPath, Exception ex)
    {
        var expected = OperatingSystem.IsWindows()
            ? "avcodec-62.dll, avutil-60.dll, avformat-62.dll, swscale-9.dll, swresample-6.dll"
            : "libavcodec.so.62, libavutil.so.60, libavformat.so.62, libswscale.so.9, libswresample.so.6";

        var where = string.IsNullOrEmpty(searchPath)
            ? "the system library search path"
            : searchPath;

        var hint = OperatingSystem.IsWindows()
            ? "A static FFmpeg build (for example `winget install Gyan.FFmpeg`) ships only " +
              "ffmpeg.exe and will NOT work — a shared build is required."
            : "Install the FFmpeg shared runtime for your distribution (for example " +
              "libavcodec-dev / ffmpeg-libs).";

        return $"""
            FFmpeg could not be loaded from {where}.

            FFmpeg.AutoGen 8.1.0 binds FFmpeg 8.x and requires the SHARED libraries:
              {expected}

            {hint}

            Set FFmpegLoader.OverridePath to the directory containing those files, or place
            them in runtimes/{CurrentRid()}/native next to the app.

            Underlying error: {ex.GetType().Name}: {ex.Message}
            """;
    }

    /// <summary>
    /// Throws if FFmpeg is unusable. Call from the media engine, which genuinely
    /// cannot proceed without it.
    /// </summary>
    public static void EnsureAvailable()
    {
        var availability = Initialize();
        if (!availability.IsAvailable)
            throw new InvalidOperationException(availability.Diagnostic ?? "FFmpeg is unavailable.");
    }

    /// <summary>
    /// Portable RID for native asset probing.
    /// <see cref="RuntimeInformation.RuntimeIdentifier"/> can report a distro-specific
    /// value such as <c>ubuntu.22.04-x64</c>, which never matches the folder names laid
    /// down under <c>runtimes/</c>.
    /// </summary>
    public static string CurrentRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64"
        };

        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsAndroid()) return $"android-{arch}";
        if (OperatingSystem.IsLinux()) return $"linux-{arch}";

        return $"unknown-{arch}";
    }

    /// <summary>Human-readable FFmpeg error for a negative return code.</summary>
    public static unsafe string DescribeError(int errorCode)
    {
        const int bufferSize = 1024;
        var buffer = stackalloc byte[bufferSize];

        if (ffmpeg.av_strerror(errorCode, buffer, bufferSize) < 0)
            return $"Unknown FFmpeg error {errorCode}.";

        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"FFmpeg error {errorCode}.";
    }

    /// <summary>Throws with a decoded message when <paramref name="errorCode"/> is negative.</summary>
    public static int ThrowIfError(int errorCode, string operation)
    {
        if (errorCode < 0)
            throw new FFmpegException($"{operation} failed: {DescribeError(errorCode)}", errorCode);

        return errorCode;
    }
}

public sealed class FFmpegException(string message, int errorCode) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
}
