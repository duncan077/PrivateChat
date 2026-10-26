namespace ChatApp.Media.Audio;

/// <summary>
/// Voice activity detection for the speaking indicators in the participant list.
/// <para>
/// Replaces the <c>AudioContext</c> analyser loop the Blazor client ran per participant.
/// Thresholds match that implementation so the indicators behave the same: RMS above
/// 0.035 of full scale counts as speech, and the state is held for 700 ms after the
/// level drops so normal pauses between words do not make the indicator flicker.
/// </para>
/// </summary>
public sealed class SpeakingDetector
{
    /// <summary>RMS as a fraction of full scale. Matches the Blazor client's 0.035.</summary>
    public const double DefaultThreshold = 0.035;

    /// <summary>How long "speaking" persists after the level drops.</summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMilliseconds(700);

    private readonly double _threshold;
    private readonly TimeSpan _hold;
    private TimeSpan _lastAboveThreshold = TimeSpan.MinValue;

    public bool IsSpeaking { get; private set; }

    /// <summary>Most recent RMS level, 0.0 to 1.0. Drives the settings-dialog meter.</summary>
    public double Level { get; private set; }

    public SpeakingDetector(double threshold = DefaultThreshold, TimeSpan? hold = null)
    {
        _threshold = threshold;
        _hold = hold ?? DefaultHold;
    }

    /// <summary>
    /// Feeds a block of samples and returns true when the speaking state changed.
    /// <para>
    /// <paramref name="now"/> is passed in rather than read from the clock so the hold
    /// behaviour is testable without waiting in real time.
    /// </para>
    /// </summary>
    public bool Update(ReadOnlySpan<short> interleaved, TimeSpan now)
    {
        Level = ComputeRms(interleaved);

        if (Level >= _threshold)
            _lastAboveThreshold = now;

        var withinHold = _lastAboveThreshold != TimeSpan.MinValue && now - _lastAboveThreshold < _hold;
        var speaking = Level >= _threshold || withinHold;

        if (speaking == IsSpeaking)
            return false;

        IsSpeaking = speaking;
        return true;
    }

    public void Reset()
    {
        IsSpeaking = false;
        Level = 0;
        _lastAboveThreshold = TimeSpan.MinValue;
    }

    /// <summary>Root-mean-square amplitude normalised to 0.0–1.0.</summary>
    private static double ComputeRms(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return 0;

        // Accumulate in double: squaring int16 across a large block overflows int64
        // only in extreme cases, but double keeps this obviously correct.
        double sum = 0;
        foreach (var sample in samples)
        {
            var normalized = sample / (double)short.MaxValue;
            sum += normalized * normalized;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}
