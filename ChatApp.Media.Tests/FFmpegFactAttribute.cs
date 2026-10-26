using ChatApp.Media.Codec;

namespace ChatApp.Media.Tests;

/// <summary>
/// A fact that skips (rather than fails) when the FFmpeg shared libraries are absent, so
/// a checkout that has not run <c>tools/fetch-ffmpeg.ps1</c> still gets a green run —
/// while the skip reason makes it obvious the codec paths went unverified.
/// </summary>
public sealed class FFmpegFactAttribute : FactAttribute
{
    public FFmpegFactAttribute()
    {
        var availability = FFmpegLoader.Initialize();
        if (!availability.IsAvailable)
            Skip = "FFmpeg shared libraries unavailable — run tools/fetch-ffmpeg.ps1.";
    }
}

/// <summary>
/// A fact for Windows-only code, so the suite still runs green on the Linux head rather
/// than failing on types that cannot exist there.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only.";
    }
}
