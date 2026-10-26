using ChatApp.Media.Audio;
using ChatApp.Media.Codec;

namespace ChatApp.Media.Tests;

/// <summary>
/// Capture delivers whatever period the device chooses; Opus needs exactly 960 stereo
/// samples per frame. Every mismatch has to be absorbed here or the RTP timestamp clock
/// drifts.
/// </summary>
public class AudioFrameAssemblerTests
{
    private static List<short[]> Collect(AudioFrameAssembler assembler, ReadOnlySpan<short> input)
    {
        var frames = new List<short[]>();
        assembler.Append(input, frame => frames.Add(frame.ToArray()));
        return frames;
    }

    [Fact]
    public void Emits_nothing_until_a_full_frame_is_available()
    {
        var assembler = new AudioFrameAssembler();

        var frames = Collect(assembler, new short[assembler.FrameLength - 2]);

        Assert.Empty(frames);
        Assert.Equal(assembler.FrameLength - 2, assembler.Pending);
    }

    [Fact]
    public void Emits_exactly_one_frame_for_an_exact_fit()
    {
        var assembler = new AudioFrameAssembler();

        var frames = Collect(assembler, new short[assembler.FrameLength]);

        Assert.Single(frames);
        Assert.Equal(assembler.FrameLength, frames[0].Length);
        Assert.Equal(0, assembler.Pending);
    }

    [Fact]
    public void Splits_a_large_buffer_into_multiple_frames_and_keeps_the_remainder()
    {
        var assembler = new AudioFrameAssembler();
        var input = new short[assembler.FrameLength * 3 + 17];

        var frames = Collect(assembler, input);

        Assert.Equal(3, frames.Count);
        Assert.Equal(17, assembler.Pending);
    }

    [Fact]
    public void Joins_samples_across_calls_without_losing_any()
    {
        // A 10ms device period must produce one 20ms frame every two callbacks.
        var assembler = new AudioFrameAssembler();
        var half = assembler.FrameLength / 2;

        Assert.Empty(Collect(assembler, new short[half]));
        Assert.Single(Collect(assembler, new short[half]));
    }

    [Fact]
    public void Preserves_sample_order_across_a_frame_boundary()
    {
        var assembler = new AudioFrameAssembler();
        var total = assembler.FrameLength * 2;

        var input = new short[total];
        for (var i = 0; i < total; i++)
            input[i] = (short)(i % short.MaxValue);

        // Feed in awkward chunks to force the split path.
        var frames = new List<short[]>();
        var offset = 0;
        foreach (var chunk in new[] { 7, 331, 1024, 99 }.Concat(Enumerable.Repeat(512, 64)))
        {
            if (offset >= total) break;
            var take = Math.Min(chunk, total - offset);
            assembler.Append(input.AsSpan(offset, take), f => frames.Add(f.ToArray()));
            offset += take;
        }

        Assert.Equal(2, frames.Count);

        var flattened = frames.SelectMany(f => f).ToArray();
        Assert.Equal(input.Take(flattened.Length), flattened);
    }

    [Fact]
    public void Reset_discards_a_partial_frame()
    {
        // Switching microphones must not splice the old device's tail onto the new one.
        var assembler = new AudioFrameAssembler();

        Collect(assembler, new short[100]);
        Assert.Equal(100, assembler.Pending);

        assembler.Reset();
        Assert.Equal(0, assembler.Pending);

        var frames = Collect(assembler, new short[assembler.FrameLength]);
        Assert.Single(frames);
    }

    [Fact]
    public void Rejects_invalid_configuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFrameAssembler(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFrameAssembler(960, 0));
    }

    [Fact]
    public void Default_matches_opus_20ms_stereo()
        => Assert.Equal(OpusEncoder.FrameSizeSamples * OpusEncoder.Channels, new AudioFrameAssembler().FrameLength);
}

/// <summary>
/// Resampling runs against real swresample: rate conversion needs a proper filter, and
/// getting the channel mix wrong is silent until someone joins with a mono headset.
/// </summary>
public class AudioResamplerTests
{
    [FFmpegFact]
    public void Passes_through_when_the_format_already_matches()
    {
        using var resampler = new AudioResampler();

        var input = new short[960 * 2];
        var output = resampler.Convert(input, 48_000, 2);

        Assert.True(resampler.IsPassthrough);
        Assert.Equal(input.Length, output.Length);
    }

    [FFmpegFact]
    public void Upmixes_mono_to_stereo()
    {
        using var resampler = new AudioResampler();

        var input = new short[480];
        for (var i = 0; i < input.Length; i++)
            input[i] = 1000;

        var output = resampler.Convert(input, 48_000, 1).ToArray();

        Assert.False(resampler.IsPassthrough);
        // Same number of frames, twice the interleaved samples.
        Assert.Equal(input.Length * 2, output.Length);
    }

    [FFmpegFact]
    public void Converts_44100_to_48000()
    {
        using var resampler = new AudioResampler();

        // 100 ms at 44.1 kHz stereo.
        var input = new short[4410 * 2];
        var output = resampler.Convert(input, 44_100, 2);

        Assert.False(resampler.IsPassthrough);

        // ~4800 frames out, allowing for the resampler's internal delay on the first call.
        var framesOut = output.Length / 2;
        Assert.InRange(framesOut, 4600, 4900);
    }

    [FFmpegFact]
    public void Preserves_a_tone_through_rate_conversion()
    {
        // Guards against the resampler emitting silence or noise, which a length-only
        // assertion would happily accept.
        using var resampler = new AudioResampler();

        var input = new short[4410 * 2];
        for (var i = 0; i < 4410; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / 44_100.0) * 10_000);
            input[i * 2] = value;
            input[i * 2 + 1] = value;
        }

        var output = resampler.Convert(input, 44_100, 2).ToArray();

        Assert.NotEmpty(output);

        var peak = output.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, 8_000, 12_000);
    }

    [FFmpegFact]
    public void Handles_a_source_format_change_mid_stream()
    {
        // Switching microphones changes the rate; the context must be rebuilt rather
        // than reused with stale settings.
        using var resampler = new AudioResampler();

        var first = resampler.Convert(new short[4410 * 2], 44_100, 2);
        Assert.NotEqual(0, first.Length);

        var second = resampler.Convert(new short[960], 16_000, 1);
        Assert.NotEqual(0, second.Length);
    }

    [FFmpegFact]
    public void Empty_input_produces_empty_output()
    {
        using var resampler = new AudioResampler();

        Assert.True(resampler.Convert(ReadOnlySpan<short>.Empty, 44_100, 2).IsEmpty);
    }
}
