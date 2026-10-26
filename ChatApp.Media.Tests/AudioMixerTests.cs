using ChatApp.Media.Audio;

namespace ChatApp.Media.Tests;

/// <summary>
/// The mixer turns several participants' audio into one stream.
/// <para>
/// Its central rule is <b>whole frames only</b>. An earlier version emitted whatever was
/// buffered and zero-padded the rest, which put a hard discontinuity at the end of most
/// frames and buzzed at the pump rate — 20 ms packets never align perfectly with 20 ms
/// ticks, so the partial case was the norm. A source now primes with a cushion first and
/// goes silent cleanly when starved.
/// </para>
/// </summary>
public class AudioMixerTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const long AliceFeed = 1;
    private const long BobFeed = 2;

    /// <summary>Frame size used by these tests; the real pump uses 960 x 2.</summary>
    private const int Frame = 64;

    /// <summary>Frames of cushion a source builds before it starts contributing.</summary>
    private const int PrimeFrames = 3;

    /// <summary>Consecutive starved ticks before the cushion is rebuilt.</summary>
    private const int SustainedUnderruns = 25;

    private static short[] Constant(int length, short value)
    {
        var samples = new short[length];
        samples.AsSpan().Fill(value);
        return samples;
    }

    /// <summary>Submits enough audio to get the source past priming.</summary>
    private static void Prime(AudioMixer mixer, Guid user, short value)
        => mixer.Submit(user == Alice ? AliceFeed : BobFeed, user, Constant(Frame * PrimeFrames, value));

    [Fact]
    public void Reports_silence_when_no_sources_exist()
    {
        var mixer = new AudioMixer();
        var output = new short[Frame];

        Assert.False(mixer.MixInto(output));
        Assert.All(output, s => Assert.Equal(0, s));
    }

    [Fact]
    public void Reports_silence_when_a_source_has_no_buffered_audio()
    {
        var mixer = new AudioMixer();
        mixer.Submit(AliceFeed, Alice, Constant(1, 0));

        Assert.False(mixer.MixInto(new short[Frame]));
    }

    [Fact]
    public void Withholds_audio_until_the_priming_cushion_is_built()
    {
        // Half a frame is not enough to start; emitting it would zero-pad the remainder.
        var mixer = new AudioMixer();
        mixer.Submit(AliceFeed, Alice, Constant(Frame / 2, 1000));

        Assert.False(mixer.MixInto(new short[Frame]));
    }

    [Fact]
    public void Never_emits_a_partially_filled_frame()
    {
        // The regression: one and a half frames must yield exactly one full frame, then
        // silence — not a full frame followed by a half-frame padded with zeros.
        var mixer = new AudioMixer();
        mixer.Submit(AliceFeed, Alice, Constant(Frame * PrimeFrames, 1000));

        var first = new short[Frame];
        Assert.True(mixer.MixInto(first));
        Assert.All(first, s => Assert.Equal(1000, s));

        var second = new short[Frame];
        Assert.True(mixer.MixInto(second));
        Assert.All(second, s => Assert.Equal(1000, s));

        var third = new short[Frame];
        Assert.True(mixer.MixInto(third));
        Assert.All(third, s => Assert.Equal(1000, s));

        // Nothing left: silent rather than a padded fragment.
        var fourth = new short[Frame];
        Assert.False(mixer.MixInto(fourth));
        Assert.All(fourth, s => Assert.Equal(0, s));
    }

    [Fact]
    public void Passes_a_single_source_through_unchanged()
    {
        var mixer = new AudioMixer();
        var output = new short[Frame];

        Prime(mixer, Alice, 1000);

        Assert.True(mixer.MixInto(output));
        Assert.All(output, s => Assert.Equal(1000, s));
    }

    [Fact]
    public void Sums_two_sources()
    {
        var mixer = new AudioMixer();
        var output = new short[Frame];

        Prime(mixer, Alice, 1000);
        Prime(mixer, Bob, 500);

        Assert.True(mixer.MixInto(output));
        Assert.All(output, s => Assert.Equal(1500, s));
    }

    [Fact]
    public void Clamps_instead_of_wrapping_on_overflow()
    {
        // Summing in int16 would wrap and turn two loud speakers into crackling.
        var mixer = new AudioMixer();
        var output = new short[Frame];

        Prime(mixer, Alice, 30000);
        Prime(mixer, Bob, 30000);

        Assert.True(mixer.MixInto(output));
        Assert.All(output, s => Assert.Equal(short.MaxValue, s));
    }

    [Fact]
    public void Clamps_negative_overflow_too()
    {
        var mixer = new AudioMixer();
        var output = new short[Frame];

        Prime(mixer, Alice, -30000);
        Prime(mixer, Bob, -30000);

        Assert.True(mixer.MixInto(output));
        Assert.All(output, s => Assert.Equal(short.MinValue, s));
    }

    [Fact]
    public void Applies_per_participant_gain()
    {
        var mixer = new AudioMixer();
        var output = new short[Frame];

        mixer.SetGain(Alice, 0.5);
        Prime(mixer, Alice, 1000);

        Assert.True(mixer.MixInto(output));
        Assert.All(output, s => Assert.Equal(500, s));
    }

    [Fact]
    public void Zero_gain_mutes_a_participant_entirely()
    {
        var mixer = new AudioMixer();
        var output = new short[Frame];

        mixer.SetGain(Alice, 0.0);
        Prime(mixer, Alice, 5000);

        Assert.False(mixer.MixInto(output));
        Assert.All(output, s => Assert.Equal(0, s));
    }

    [Fact]
    public void An_isolated_late_packet_costs_one_tick_not_a_whole_rebuild()
    {
        // The regression that caused the buzz: dropping back to unprimed after a single
        // miss forced the full cushion to be rebuilt, so one late packet silenced several
        // consecutive ticks. Since packets arrive at the same average rate the pump
        // drains them, that pattern repeated forever.
        var mixer = new AudioMixer();

        Prime(mixer, Alice, 700);
        for (var i = 0; i < PrimeFrames; i++)
            Assert.True(mixer.MixInto(new short[Frame]));

        // Starved for one tick.
        Assert.False(mixer.MixInto(new short[Frame]));

        // A single frame arrives and plays immediately — no rebuild.
        mixer.Submit(AliceFeed, Alice, Constant(Frame, 700));
        Assert.True(mixer.MixInto(new short[Frame]));
    }

    [Fact]
    public void Sustained_starvation_rebuilds_the_cushion()
    {
        // A speaker who stops talking sends nothing at all (WebRTC uses discontinuous
        // transmission). When they resume, the cushion should be re-established rather
        // than playback starting on an empty buffer.
        var mixer = new AudioMixer();

        Prime(mixer, Alice, 700);
        for (var i = 0; i < PrimeFrames; i++)
            Assert.True(mixer.MixInto(new short[Frame]));

        for (var i = 0; i < SustainedUnderruns; i++)
            Assert.False(mixer.MixInto(new short[Frame]));

        // Now unprimed: one frame is not enough to resume.
        mixer.Submit(AliceFeed, Alice, Constant(Frame, 700));
        Assert.False(mixer.MixInto(new short[Frame]));

        // A full cushion resumes playback.
        mixer.Submit(AliceFeed, Alice, Constant(Frame * (PrimeFrames - 1), 700));
        Assert.True(mixer.MixInto(new short[Frame]));
    }

    [Fact]
    public void Drops_the_oldest_audio_when_a_source_overruns()
    {
        // A source running ahead resyncs at the live edge rather than accumulating delay.
        var mixer = new AudioMixer(channels: 2, bufferedMilliseconds: 60);
        var capacity = 48_000 * 60 / 1000 * 2;

        mixer.Submit(AliceFeed, Alice, Constant(capacity, 100));
        mixer.Submit(AliceFeed, Alice, Constant(capacity, 200));

        var output = new short[capacity / PrimeFrames];
        Assert.True(mixer.MixInto(output));
        Assert.All(output, s => Assert.Equal(200, s));
    }

    [Fact]
    public void Handles_a_submission_larger_than_the_whole_buffer()
    {
        var mixer = new AudioMixer(channels: 2, bufferedMilliseconds: 60);
        var capacity = 48_000 * 60 / 1000 * 2;

        var oversized = new short[capacity * 3];
        oversized.AsSpan(..(capacity * 2)).Fill(111);
        oversized.AsSpan(capacity * 2).Fill(222);

        mixer.Submit(AliceFeed, Alice, oversized);

        var output = new short[capacity / PrimeFrames];
        Assert.True(mixer.MixInto(output));

        // Keeps the tail, which is the most recent audio.
        Assert.All(output, s => Assert.Equal(222, s));
    }

    [Fact]
    public void Removing_a_source_stops_its_contribution()
    {
        var mixer = new AudioMixer();
        var output = new short[Frame];

        Prime(mixer, Alice, 1000);
        Prime(mixer, Bob, 1000);
        mixer.RemoveSource(BobFeed);

        Assert.True(mixer.MixInto(output));
        Assert.All(output, s => Assert.Equal(1000, s));
        Assert.Equal(1, mixer.SourceCount);
    }

    [Fact]
    public void Gain_survives_a_participant_reconnecting()
    {
        // Saved per-user volumes are applied before any audio arrives.
        var mixer = new AudioMixer();

        mixer.SetGain(Alice, 0.25);
        Assert.Equal(0.25, mixer.GetGain(Alice), 3);

        Prime(mixer, Alice, 4000);

        var output = new short[Frame];
        Assert.True(mixer.MixInto(output));
        Assert.All(output, s => Assert.Equal(1000, s));
    }

    [Fact]
    public void Steady_supply_matching_the_pump_produces_unbroken_audio()
    {
        // The live failure: ~50 packets/sec arriving against ~50 ticks/sec produced audio
        // on only half the ticks. With supply equal to demand every tick must play.
        var mixer = new AudioMixer();

        Prime(mixer, Alice, 700);

        var played = 0;
        for (var tick = 0; tick < 200; tick++)
        {
            // One frame arrives per tick, exactly matching what the pump drains.
            mixer.Submit(AliceFeed, Alice, Constant(Frame, 700));

            if (mixer.MixInto(new short[Frame]))
                played++;
        }

        Assert.Equal(200, played);
        Assert.Equal(0, Assert.Single(mixer.GetDiagnostics()).Underruns);
    }

    [Fact]
    public void Diagnostics_expose_buffer_depth_and_underruns()
    {
        var mixer = new AudioMixer();

        Prime(mixer, Alice, 1000);
        Assert.True(mixer.MixInto(new short[Frame]));

        var diagnostics = Assert.Single(mixer.GetDiagnostics());
        Assert.Equal(Alice, diagnostics.UserId);
        Assert.Equal(Frame * (PrimeFrames - 1), diagnostics.BufferedSamples);
        Assert.Equal(0, diagnostics.Underruns);

        // Drain the rest, then starve.
        for (var i = 0; i < PrimeFrames - 1; i++)
            Assert.True(mixer.MixInto(new short[Frame]));

        Assert.False(mixer.MixInto(new short[Frame]));

        Assert.Equal(1, Assert.Single(mixer.GetDiagnostics()).Underruns);
    }

    [Fact]
    public void Two_feeds_from_one_participant_do_not_share_a_buffer()
    {
        // The live failure: one person published two audio feeds, both were merged into a
        // single ring keyed by user, and it filled at twice the pump's drain rate — "in"
        // measured 200% of expected and the buffer pinned at its cap, adding a fixed
        // delay before it started discarding.
        var mixer = new AudioMixer();

        const long cameraFeed = 10;
        const long screenShareFeed = 11;

        mixer.Submit(cameraFeed, Alice, Constant(Frame * PrimeFrames, 1000));
        mixer.Submit(screenShareFeed, Alice, Constant(Frame * PrimeFrames, 500));

        Assert.Equal(2, mixer.SourceCount);

        var output = new short[Frame];
        Assert.True(mixer.MixInto(output));

        // Summed, not queued behind one another.
        Assert.All(output, sample => Assert.Equal(1500, sample));

        // Each drains independently, so neither accumulates a backlog.
        foreach (var source in mixer.GetDiagnostics())
            Assert.Equal(Frame * (PrimeFrames - 1), source.BufferedSamples);
    }

    [Fact]
    public void Gain_applies_to_every_feed_a_participant_publishes()
    {
        var mixer = new AudioMixer();
        mixer.SetGain(Alice, 0.5);

        mixer.Submit(10, Alice, Constant(Frame * PrimeFrames, 1000));
        mixer.Submit(11, Alice, Constant(Frame * PrimeFrames, 1000));

        var output = new short[Frame];
        Assert.True(mixer.MixInto(output));

        // Both feeds halved: 500 + 500.
        Assert.All(output, sample => Assert.Equal(1000, sample));
    }

    [Fact]
    public void Gain_set_before_a_feed_arrives_still_applies_to_it()
    {
        var mixer = new AudioMixer();
        mixer.SetGain(Alice, 0.25);

        mixer.Submit(AliceFeed, Alice, Constant(Frame * PrimeFrames, 4000));

        var output = new short[Frame];
        Assert.True(mixer.MixInto(output));
        Assert.All(output, sample => Assert.Equal(1000, sample));
    }

    [Fact]
    public void Removing_one_feed_leaves_the_participants_other_feed_playing()
    {
        var mixer = new AudioMixer();

        mixer.Submit(10, Alice, Constant(Frame * PrimeFrames, 1000));
        mixer.Submit(11, Alice, Constant(Frame * PrimeFrames, 500));

        mixer.RemoveSource(11);

        var output = new short[Frame];
        Assert.True(mixer.MixInto(output));
        Assert.All(output, sample => Assert.Equal(1000, sample));
        Assert.Equal(1, mixer.SourceCount);
    }

    [Fact]
    public void RemoveUser_drops_all_of_that_participants_feeds()
    {
        var mixer = new AudioMixer();

        mixer.Submit(10, Alice, Constant(Frame * PrimeFrames, 1000));
        mixer.Submit(11, Alice, Constant(Frame * PrimeFrames, 1000));
        mixer.Submit(BobFeed, Bob, Constant(Frame * PrimeFrames, 700));

        mixer.RemoveUser(Alice);

        Assert.Equal(1, mixer.SourceCount);

        var output = new short[Frame];
        Assert.True(mixer.MixInto(output));
        Assert.All(output, sample => Assert.Equal(700, sample));
    }
}
