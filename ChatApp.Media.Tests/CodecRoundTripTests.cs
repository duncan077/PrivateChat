using ChatApp.Core.Media;
using ChatApp.Media.Codec;

namespace ChatApp.Media.Tests;

/// <summary>
/// End-to-end exercise of the real FFmpeg codecs: encode synthetic frames, decode them
/// back, and check the properties the RTP layer depends on.
/// </summary>
public class CodecRoundTripTests
{
    private const int Width = 320;
    private const int Height = 240;
    private const int FrameRate = 30;

    // Software-only so the result does not depend on which GPU the test machine has —
    // decode included, or a machine with d3d11va would produce NV12 where these expect
    // I420.
    private static VideoEncoderPolicy SoftwarePolicy() =>
        new() { AllowHardwareEncode = false, AllowHardwareDecode = false };

    /// <summary>A moving gradient, so successive frames actually differ and P-frames have work to do.</summary>
    private static byte[] MakeI420Frame(int width, int height, int frameIndex)
    {
        var lumaSize = width * height;
        var chromaSize = (width / 2) * (height / 2);
        var buffer = new byte[lumaSize + chromaSize * 2];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            buffer[y * width + x] = (byte)((x + y + frameIndex * 8) & 0xFF);

        buffer.AsSpan(lumaSize, chromaSize).Fill((byte)(128 + (frameIndex % 16)));
        buffer.AsSpan(lumaSize + chromaSize, chromaSize).Fill(128);

        return buffer;
    }

    [FFmpegFact]
    public void Encodes_and_decodes_h264()
    {
        using var encoder = VideoEncoder.Create(
            VideoCodec.H264, Width, Height, FrameRate, bitrateKbps: 500, SoftwarePolicy());

        using var decoder = VideoDecoder.Create(VideoCodec.H264, SoftwarePolicy());

        Assert.Equal("libx264", encoder.EncoderName);
        Assert.False(encoder.IsHardware);

        var decodedCount = 0;
        var decodedWidth = 0;
        var decodedHeight = 0;

        for (var i = 0; i < 30; i++)
        {
            var frame = MakeI420Frame(Width, Height, i);

            encoder.Encode(frame, Width, Height, VideoPixelFormat.I420, (payload, _, _) =>
            {
                // Copy: the span is only valid inside the callback, and the decoder call
                // below would otherwise reuse the encoder's buffer.
                var packet = payload.ToArray();

                decoder.Decode(packet, (in VideoFrame decoded) =>
                {
                    decodedCount++;
                    decodedWidth = decoded.Width;
                    decodedHeight = decoded.Height;

                    var w = decoded.Width;
                    var h = decoded.Height;

                    Assert.Equal(VideoPixelFormat.I420, decoded.Format);
                    Assert.Equal(w * h + 2 * ((w + 1) / 2) * ((h + 1) / 2), decoded.Data.Length);
                });
            });
        }

        Assert.True(decodedCount > 0, "no frames decoded");
        Assert.Equal(Width, decodedWidth);
        Assert.Equal(Height, decodedHeight);
        Assert.True(decoder.HasDecodedFrame);
    }

    [FFmpegFact]
    public void Decodes_through_whatever_hardware_this_machine_has()
    {
        // The encode side stays software so every machine feeds the decoder an identical
        // stream; only the decode path varies. A machine with no usable GPU falls through
        // to software and must still produce frames — that fallback is half the point.
        using var encoder = VideoEncoder.Create(
            VideoCodec.H264, Width, Height, FrameRate, bitrateKbps: 500, SoftwarePolicy());

        using var decoder = VideoDecoder.Create(
            VideoCodec.H264, new VideoEncoderPolicy { AllowHardwareDecode = true });

        var formats = new HashSet<VideoPixelFormat>();
        var decodedCount = 0;

        for (var i = 0; i < 30; i++)
        {
            encoder.Encode(MakeI420Frame(Width, Height, i), Width, Height, VideoPixelFormat.I420,
                (payload, _, _) =>
                {
                    var packet = payload.ToArray();

                    decoder.Decode(packet, (in VideoFrame decoded) =>
                    {
                        decodedCount++;
                        formats.Add(decoded.Format);

                        Assert.Equal(Width, decoded.Width);
                        Assert.Equal(Height, decoded.Height);
                        Assert.False(decoded.Data.IsEmpty);
                    });
                });
        }

        Assert.True(decodedCount > 0, $"no frames decoded via {decoder.DecoderName}");
        Assert.False(decoder.HardwareFailed, $"{decoder.DecoderName} gave up mid-stream");

        // NV12 is what comes back off a GPU, I420 is what software produces. The renderer
        // scales either; anything else would reach it as a black tile.
        Assert.All(formats, format =>
            Assert.True(format is VideoPixelFormat.Nv12 or VideoPixelFormat.I420, $"unrenderable {format}"));

        // The reported path has to match what actually ran: the health log is the only
        // way to tell the two apart on someone else's machine.
        if (decoder.IsHardware)
            Assert.Contains(VideoPixelFormat.Nv12, formats);
    }

    [FFmpegFact]
    public void Hardware_decode_reproduces_software_decode_exactly()
    {
        // H.264 reconstruction is bit-exact by specification, so a GPU and a CPU decoding
        // the same stream must agree byte for byte. That makes this the one test that can
        // actually catch a read-back landing at the wrong stride, in the wrong buffer, or
        // a frame late — mistakes that a "did we get frames" test sails straight past.
        // On a machine with no usable GPU both paths are software and this still holds.
        var packets = new List<byte[]>();

        using (var encoder = VideoEncoder.Create(
                   VideoCodec.H264, Width, Height, FrameRate, bitrateKbps: 2000, SoftwarePolicy()))
        {
            for (var i = 0; i < 20; i++)
            {
                encoder.Encode(MakeI420Frame(Width, Height, i), Width, Height, VideoPixelFormat.I420,
                    (payload, _, _) => packets.Add(payload.ToArray()));
            }
        }

        var software = DecodeLumaPlanes(packets, allowHardware: false);
        var hardware = DecodeLumaPlanes(packets, allowHardware: true);

        Assert.NotEmpty(software.Planes);
        Assert.Equal(software.Planes.Count, hardware.Planes.Count);

        for (var i = 0; i < software.Planes.Count; i++)
        {
            Assert.True(
                software.Planes[i].AsSpan().SequenceEqual(hardware.Planes[i]),
                $"frame {i} differs between {software.Decoder} and {hardware.Decoder}");
        }

        // A read-back that quietly stopped landing in our buffer would leave every frame
        // equal to the one before it, which the comparison above would not notice.
        for (var i = 1; i < hardware.Planes.Count; i++)
            Assert.False(hardware.Planes[i].AsSpan().SequenceEqual(hardware.Planes[i - 1]), $"frame {i} never changed");
    }

    /// <summary>Decodes every packet and copies out each picture's luma plane.</summary>
    private static (List<byte[]> Planes, string Decoder) DecodeLumaPlanes(List<byte[]> packets, bool allowHardware)
    {
        using var decoder = VideoDecoder.Create(
            VideoCodec.H264, new VideoEncoderPolicy { AllowHardwareDecode = allowHardware });

        var planes = new List<byte[]>();

        foreach (var packet in packets)
        {
            decoder.Decode(packet, (in VideoFrame frame) =>
            {
                // Y is plane 0 in both I420 and NV12, so the two paths are comparable
                // without converting either of them.
                var luma = new byte[frame.Width * frame.Height];

                for (var y = 0; y < frame.Height; y++)
                {
                    frame.Data
                        .Slice(frame.PlaneOffsets[0] + y * frame.PlaneStrides[0], frame.Width)
                        .CopyTo(luma.AsSpan(y * frame.Width));
                }

                planes.Add(luma);
            });
        }

        return (planes, decoder.DecoderName);
    }

    [FFmpegFact]
    public void First_packet_is_a_keyframe()
    {
        // A late joiner can only start decoding from a keyframe, so the stream must open
        // with one rather than relying on a GOP timer we deliberately disabled.
        using var encoder = VideoEncoder.Create(
            VideoCodec.H264, Width, Height, FrameRate, bitrateKbps: 500, SoftwarePolicy());

        bool? firstWasKeyframe = null;

        encoder.Encode(MakeI420Frame(Width, Height, 0), Width, Height, VideoPixelFormat.I420,
            (_, _, isKeyframe) => firstWasKeyframe ??= isKeyframe);

        Assert.True(firstWasKeyframe);
    }

    [FFmpegFact]
    public void RequestKeyframe_forces_an_idr()
    {
        // This is the PLI path: gop_size is int.MaxValue, so without an explicit request
        // there would never be a second keyframe.
        using var encoder = VideoEncoder.Create(
            VideoCodec.H264, Width, Height, FrameRate, bitrateKbps: 500, SoftwarePolicy());

        var keyframeFlags = new List<bool>();
        void Encode(int index) => encoder.Encode(
            MakeI420Frame(Width, Height, index), Width, Height, VideoPixelFormat.I420,
            (_, _, isKeyframe) => keyframeFlags.Add(isKeyframe));

        for (var i = 0; i < 10; i++)
            Encode(i);

        var beforeRequest = keyframeFlags.Count;
        Assert.True(beforeRequest > 1, "expected several packets before the request");

        // Everything after the opening frame should be a P-frame.
        Assert.DoesNotContain(true, keyframeFlags.Skip(1));

        encoder.RequestKeyframe();
        for (var i = 10; i < 15; i++)
            Encode(i);

        Assert.Contains(true, keyframeFlags.Skip(beforeRequest));
    }

    [FFmpegFact]
    public void Rtp_timestamps_advance_on_the_90khz_clock()
    {
        using var encoder = VideoEncoder.Create(
            VideoCodec.H264, Width, Height, FrameRate, bitrateKbps: 500, SoftwarePolicy());

        var timestamps = new List<uint>();

        for (var i = 0; i < 10; i++)
        {
            encoder.Encode(MakeI420Frame(Width, Height, i), Width, Height, VideoPixelFormat.I420,
                (_, rtpTimestamp, _) => timestamps.Add(rtpTimestamp));
        }

        Assert.True(timestamps.Count >= 2);

        // 30fps on a 90kHz clock is exactly 3000 ticks per frame. Janus and every
        // receiver rely on this to reconstruct timing.
        var expectedStep = (uint)(VideoEncoder.ClockRate / FrameRate);
        for (var i = 1; i < timestamps.Count; i++)
            Assert.Equal(expectedStep, timestamps[i] - timestamps[i - 1]);
    }

    [FFmpegFact]
    public void Scales_and_converts_a_mismatched_source()
    {
        // Capture rarely matches the negotiated resolution; the encoder is expected to
        // rescale rather than reject the frame.
        using var encoder = VideoEncoder.Create(
            VideoCodec.H264, Width, Height, FrameRate, bitrateKbps: 500, SoftwarePolicy());

        var bgra = new byte[640 * 480 * 4];
        Random.Shared.NextBytes(bgra);

        var produced = 0;
        encoder.Encode(bgra, 640, 480, VideoPixelFormat.Bgra32, (_, _, _) => produced++);

        Assert.True(produced > 0, "no packet produced from a BGRA 640x480 source");
    }

    [FFmpegFact]
    public void Odd_dimensions_are_rounded_down_to_even()
    {
        // 4:2:0 chroma cannot represent odd dimensions; libx264 would fail to open.
        using var encoder = VideoEncoder.Create(
            VideoCodec.H264, 321, 241, FrameRate, bitrateKbps: 500, SoftwarePolicy());

        Assert.Equal(320, encoder.Width);
        Assert.Equal(240, encoder.Height);
    }

    [FFmpegFact]
    public void Encodes_and_decodes_opus()
    {
        using var encoder = OpusEncoder.Create();

        // Must match the encoder. RTP Opus has no extradata, so the decoder cannot infer
        // this and a mismatch is silent.
        using var decoder = OpusDecoder.Create(OpusEncoder.Channels);

        var samples = new short[OpusEncoder.FrameSizeSamples * OpusEncoder.Channels];
        for (var i = 0; i < OpusEncoder.FrameSizeSamples; i++)
        {
            // 440 Hz tone, both channels.
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / OpusEncoder.SampleRate) * 8000);
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        var decodedSamples = 0;
        var decodedChannels = 0;
        var decodedRate = 0;

        // Opus needs a few frames to prime before it emits decodable output.
        for (var frame = 0; frame < 5; frame++)
        {
            encoder.Encode(samples, (payload, _, _) =>
            {
                var packet = payload.ToArray();
                decoder.Decode(packet, (pcm, channels, sampleRate) =>
                {
                    decodedSamples += pcm.Length;
                    decodedChannels = channels;
                    decodedRate = sampleRate;
                });
            });
        }

        Assert.True(decodedSamples > 0, "no PCM decoded");
        Assert.Equal(OpusEncoder.Channels, decodedChannels);
        Assert.Equal(OpusEncoder.SampleRate, decodedRate);
    }

    [FFmpegFact]
    public void Opus_rejects_a_wrong_sized_buffer()
    {
        using var encoder = OpusEncoder.Create();

        // A silently truncated or padded frame would desynchronise the RTP timestamp
        // clock, so this has to be loud.
        Assert.Throws<ArgumentException>(() => encoder.Encode(new short[100], (_, _, _) => { }));
    }

    [FFmpegFact]
    public void Opus_timestamps_advance_by_the_frame_size()
    {
        using var encoder = OpusEncoder.Create();

        var samples = new short[OpusEncoder.FrameSizeSamples * OpusEncoder.Channels];
        var timestamps = new List<uint>();

        for (var i = 0; i < 5; i++)
            encoder.Encode(samples, (_, rtpTimestamp, _) => timestamps.Add(rtpTimestamp));

        Assert.True(timestamps.Count >= 2);

        // The Opus RTP clock is 48 kHz, so one 20 ms frame is exactly 960 ticks.
        for (var i = 1; i < timestamps.Count; i++)
            Assert.Equal((uint)OpusEncoder.FrameSizeSamples, timestamps[i] - timestamps[i - 1]);
    }
}
