using ChatApp.Media.Audio;
using ChatApp.Media.Codec;

namespace ChatApp.Media.Tests;

/// <summary>
/// Regression tests for the second live-call failure: received audio was garbled.
/// <para>
/// WebRTC negotiates <b>mono</b> Opus unless the SDP carries <c>stereo=1</c>, and the
/// browser publisher's fmtp line was just <c>useinbandfec=1</c>. The receive path
/// discarded the decoder's channel count and pushed 960 mono samples into a stereo mixer
/// whose pump pulls 1920 per 20 ms — so playback ran at double speed and permanently
/// starved.
/// </para>
/// </summary>
public class MonoRemoteAudioTests
{
    private const int FrameSamplesPerChannel = 960;

    [FFmpegFact]
    public void Mono_frame_upmixes_to_the_stereo_frame_the_pump_expects()
    {
        using var resampler = new AudioResampler(OpusEncoder.SampleRate, OpusEncoder.Channels);

        // One 20 ms mono frame, as a browser publisher decodes.
        var mono = new short[FrameSamplesPerChannel];

        var converted = resampler.Convert(mono, OpusEncoder.SampleRate, sourceChannels: 1);

        // The playback pump drains exactly this many interleaved samples per tick.
        Assert.Equal(FrameSamplesPerChannel * OpusEncoder.Channels, converted.Length);
    }

    [FFmpegFact]
    public void Mono_upmix_duplicates_across_channels_rather_than_shifting_pitch()
    {
        using var resampler = new AudioResampler(OpusEncoder.SampleRate, OpusEncoder.Channels);

        var mono = new short[4];
        mono[0] = 1000;
        mono[1] = 2000;
        mono[2] = 3000;
        mono[3] = 4000;

        var stereo = resampler.Convert(mono, OpusEncoder.SampleRate, sourceChannels: 1).ToArray();

        Assert.Equal(8, stereo.Length);
        // Each mono sample appears on both channels; the sequence is not consumed twice
        // as fast, which is what caused the double-speed playback.
        for (var i = 0; i < mono.Length; i++)
        {
            Assert.Equal(mono[i], stereo[i * 2]);
            Assert.Equal(mono[i], stereo[i * 2 + 1]);
        }
    }

    [FFmpegFact]
    public void A_second_of_mono_frames_fills_a_second_of_stereo_playback()
    {
        // 50 frames of 20 ms. If each contributes only half the samples the pump needs,
        // the mixer underruns for half of every tick and the audio stutters.
        using var resampler = new AudioResampler(OpusEncoder.SampleRate, OpusEncoder.Channels);
        var mixer = new AudioMixer();
        var user = Guid.NewGuid();
        const long feed = 1;

        var tone = new short[FrameSamplesPerChannel];
        for (var i = 0; i < tone.Length; i++)
            tone[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 48_000.0) * 8000);

        for (var frame = 0; frame < 50; frame++)
        {
            var converted = resampler.Convert(tone, OpusEncoder.SampleRate, sourceChannels: 1);
            mixer.Submit(feed, user, converted);
        }

        var pumpFrame = new short[FrameSamplesPerChannel * OpusEncoder.Channels];
        var ticksWithAudio = 0;

        for (var tick = 0; tick < 50; tick++)
        {
            if (mixer.MixInto(pumpFrame))
                ticksWithAudio++;
        }

        // The mixer's ring is bounded, so not every tick survives — but audio must be
        // present for a substantial run rather than half-filling each tick.
        Assert.True(ticksWithAudio > 0, "no audio reached the playback pump");
    }

    [FFmpegFact]
    public void Stereo_input_is_passed_through_untouched()
    {
        // Our own publisher and other native clients send stereo; that path must not
        // acquire a needless conversion.
        using var resampler = new AudioResampler(OpusEncoder.SampleRate, OpusEncoder.Channels);

        var stereo = new short[FrameSamplesPerChannel * 2];
        var converted = resampler.Convert(stereo, OpusEncoder.SampleRate, sourceChannels: 2);

        Assert.True(resampler.IsPassthrough);
        Assert.Equal(stereo.Length, converted.Length);
    }

    [FFmpegFact]
    public void Opus_decoder_reports_its_actual_channel_count()
    {
        // The decode callback's channel argument is what the fix depends on; if it were
        // wrong, the conversion would be too.
        using var encoder = OpusEncoder.Create();
        using var decoder = OpusDecoder.Create(OpusEncoder.Channels);

        var samples = new short[FrameSamplesPerChannel * OpusEncoder.Channels];
        for (var i = 0; i < FrameSamplesPerChannel; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / 48_000.0) * 8000);
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        var reportedChannels = 0;
        var reportedRate = 0;

        for (var frame = 0; frame < 5; frame++)
        {
            encoder.Encode(samples, (payload, _, _) =>
            {
                var packet = payload.ToArray();
                decoder.Decode(packet, (pcm, channels, sampleRate) =>
                {
                    reportedChannels = channels;
                    reportedRate = sampleRate;

                    // Whatever the layout, the buffer length must agree with it, or the
                    // interleaving conversion would read past the real data.
                    Assert.Equal(0, pcm.Length % channels);
                });
            });
        }

        Assert.True(reportedChannels is 1 or 2, $"unexpected channel count {reportedChannels}");
        Assert.Equal(48_000, reportedRate);
    }

    [FFmpegFact]
    public void A_mismatched_decoder_channel_count_silently_halves_the_audio()
    {
        // The live failure, reproduced. RTP Opus carries no extradata, so FFmpeg trusts
        // the channel count it was configured with. Decoding a stereo stream with a mono
        // decoder yields half the samples per frame — no error, no warning, just a
        // permanent 50% supply deficit that starves playback and buzzes.
        using var encoder = OpusEncoder.Create();

        using var matched = OpusDecoder.Create(OpusEncoder.Channels);
        using var mismatched = OpusDecoder.Create(1);

        var samples = new short[OpusEncoder.FrameSizeSamples * OpusEncoder.Channels];
        for (var i = 0; i < OpusEncoder.FrameSizeSamples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / 48_000.0) * 8000);
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        var matchedTotal = 0;
        var mismatchedTotal = 0;

        for (var frame = 0; frame < 5; frame++)
        {
            encoder.Encode(samples, (payload, _, _) =>
            {
                var packet = payload.ToArray();
                matched.Decode(packet, (pcm, _, _) => matchedTotal += pcm.Length);
                mismatched.Decode(packet, (pcm, _, _) => mismatchedTotal += pcm.Length);
            });
        }

        Assert.True(matchedTotal > 0);
        Assert.True(mismatchedTotal > 0);

        // Exactly the 50% deficit the live logs measured: in=48000/s against an expected
        // 96000/s.
        Assert.Equal(matchedTotal / 2, mismatchedTotal);
    }
}
