using ChatApp.Media.Audio;

namespace ChatApp.Media.Tests;

/// <summary>
/// The hold behaviour is the whole point: without it the indicator flickers on every
/// pause between words.
/// </summary>
public class SpeakingDetectorTests
{
    private static short[] Tone(double amplitudeFraction, int samples = 960)
    {
        var buffer = new short[samples];
        var amplitude = amplitudeFraction * short.MaxValue;

        for (var i = 0; i < samples; i++)
            buffer[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 48_000.0) * amplitude);

        return buffer;
    }

    private static short[] Silence(int samples = 960) => new short[samples];

    [Fact]
    public void Starts_not_speaking()
    {
        var detector = new SpeakingDetector();

        Assert.False(detector.IsSpeaking);
        Assert.Equal(0, detector.Level);
    }

    [Fact]
    public void Silence_does_not_trigger()
    {
        var detector = new SpeakingDetector();

        Assert.False(detector.Update(Silence(), TimeSpan.Zero));
        Assert.False(detector.IsSpeaking);
    }

    [Fact]
    public void Loud_audio_triggers_and_reports_a_change()
    {
        var detector = new SpeakingDetector();

        Assert.True(detector.Update(Tone(0.5), TimeSpan.Zero));
        Assert.True(detector.IsSpeaking);
    }

    [Fact]
    public void Reports_a_change_only_on_transitions()
    {
        var detector = new SpeakingDetector();

        Assert.True(detector.Update(Tone(0.5), TimeSpan.Zero));
        // Still speaking — no event, so the UI is not spammed every 20ms.
        Assert.False(detector.Update(Tone(0.5), TimeSpan.FromMilliseconds(20)));
        Assert.False(detector.Update(Tone(0.5), TimeSpan.FromMilliseconds(40)));
    }

    [Fact]
    public void Stays_speaking_through_a_short_pause()
    {
        // A gap between words must not clear the indicator.
        var detector = new SpeakingDetector();
        detector.Update(Tone(0.5), TimeSpan.Zero);

        Assert.False(detector.Update(Silence(), TimeSpan.FromMilliseconds(300)));
        Assert.True(detector.IsSpeaking);
    }

    [Fact]
    public void Clears_after_the_hold_expires()
    {
        var detector = new SpeakingDetector();
        detector.Update(Tone(0.5), TimeSpan.Zero);

        Assert.True(detector.Update(Silence(), TimeSpan.FromMilliseconds(800)));
        Assert.False(detector.IsSpeaking);
    }

    [Fact]
    public void Quiet_background_noise_stays_below_the_threshold()
    {
        // Room tone at ~1% of full scale is well under the 0.035 RMS threshold.
        var detector = new SpeakingDetector();

        Assert.False(detector.Update(Tone(0.01), TimeSpan.Zero));
        Assert.False(detector.IsSpeaking);
    }

    [Fact]
    public void Level_tracks_amplitude()
    {
        var detector = new SpeakingDetector();

        detector.Update(Tone(0.5), TimeSpan.Zero);
        var loud = detector.Level;

        detector.Update(Tone(0.1), TimeSpan.FromMilliseconds(20));
        var quiet = detector.Level;

        Assert.True(loud > quiet);
        // A full-scale sine has RMS of 1/sqrt(2); at 0.5 amplitude that is ~0.354.
        Assert.InRange(loud, 0.30, 0.40);
    }

    [Fact]
    public void Reset_clears_state_and_hold()
    {
        var detector = new SpeakingDetector();
        detector.Update(Tone(0.5), TimeSpan.Zero);

        detector.Reset();

        Assert.False(detector.IsSpeaking);
        Assert.Equal(0, detector.Level);

        // The old hold window must not resurrect the indicator.
        Assert.False(detector.Update(Silence(), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Empty_input_is_treated_as_silence()
    {
        var detector = new SpeakingDetector();

        Assert.False(detector.Update(ReadOnlySpan<short>.Empty, TimeSpan.Zero));
        Assert.Equal(0, detector.Level);
    }

    [Fact]
    public void Threshold_is_configurable()
    {
        var sensitive = new SpeakingDetector(threshold: 0.001);

        Assert.True(sensitive.Update(Tone(0.01), TimeSpan.Zero));
        Assert.True(sensitive.IsSpeaking);
    }
}
