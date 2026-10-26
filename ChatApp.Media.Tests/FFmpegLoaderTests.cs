using ChatApp.Media.Codec;

namespace ChatApp.Media.Tests;

/// <summary>
/// A missing FFmpeg must never crash the app at startup — it should degrade to a clear,
/// actionable diagnostic so a call can fail with an explanation instead of a
/// DllNotFoundException from somewhere deep in encoder setup.
/// </summary>
public class FFmpegLoaderTests
{
    [Fact]
    public void Initialize_never_throws_and_is_idempotent()
    {
        var first = FFmpegLoader.Initialize();
        var second = FFmpegLoader.Initialize();

        Assert.NotNull(first);
        // Cached, so repeated startup probes cost nothing.
        Assert.Same(first, second);
    }

    [Fact]
    public void Reports_either_a_version_or_an_actionable_diagnostic()
    {
        var availability = FFmpegLoader.Initialize();

        if (availability.IsAvailable)
        {
            Assert.False(string.IsNullOrWhiteSpace(availability.Version));
            Assert.Null(availability.Diagnostic);
            return;
        }

        Assert.Null(availability.Version);
        Assert.False(string.IsNullOrWhiteSpace(availability.Diagnostic));

        // The diagnostic has to name what is missing and where it was looked for,
        // otherwise it is just another opaque native-load failure.
        Assert.Contains("shared", availability.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avcodec", availability.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OverridePath", availability.Diagnostic, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(availability.SearchPath));
    }

    [Fact]
    public void EnsureAvailable_throws_the_diagnostic_when_unavailable()
    {
        var availability = FFmpegLoader.Initialize();

        if (availability.IsAvailable)
        {
            FFmpegLoader.EnsureAvailable();
            return;
        }

        var ex = Assert.Throws<InvalidOperationException>(FFmpegLoader.EnsureAvailable);
        Assert.Equal(availability.Diagnostic, ex.Message);
    }
}
