using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Codec;

/// <summary>
/// Opus encode/decode for the voice path.
/// <para>
/// Fixed at 48 kHz stereo with 20 ms frames — the WebRTC defaults Janus and every
/// browser negotiate. The frame size is not a free parameter: libdatachannel's Opus
/// packetizer assumes one packet per RTP packet, and the 48 kHz clock rate is baked into
/// the SDP the publisher offers.
/// </para>
/// </summary>
public sealed unsafe class OpusEncoder : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>20 ms at 48 kHz. Matches the browser default and Janus's expectation.</summary>
    public const int FrameSizeSamples = 960;

    private readonly object _gate = new();
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private long _pts;
    private bool _disposed;

    private OpusEncoder() { }

    public static OpusEncoder Create(int bitrateBps = 32_000, ILogger? logger = null)
    {
        FFmpegLoader.EnsureAvailable();

        var encoder = new OpusEncoder();
        try
        {
            encoder.Open(bitrateBps);
            (logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
                .LogInformation("Opus encoder open at {Bitrate}bps.", bitrateBps);
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    private void Open(int bitrateBps)
    {
        // libopus is preferred over FFmpeg's native encoder: better quality at
        // conferencing bitrates and it exposes the low-latency application mode.
        var codec = ffmpeg.avcodec_find_encoder_by_name("libopus");
        if (codec == null)
            codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_OPUS);

        if (codec == null)
            throw new InvalidOperationException("No Opus encoder in this FFmpeg build.");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context == null)
            throw new InvalidOperationException("Could not allocate an Opus encoder context.");

        _context->sample_rate = SampleRate;
        _context->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
        _context->bit_rate = bitrateBps;
        _context->time_base = new AVRational { num = 1, den = SampleRate };

        ffmpeg.av_channel_layout_default(&_context->ch_layout, Channels);

        AVDictionary* options = null;
        try
        {
            // "voip" biases Opus toward speech intelligibility over music fidelity.
            ffmpeg.av_dict_set(&options, "application", "voip", 0);
            ffmpeg.av_dict_set(&options, "frame_duration", "20", 0);
            // In-band FEC lets the decoder reconstruct a lost packet from the next one,
            // which matters far more than bitrate on a lossy link.
            ffmpeg.av_dict_set(&options, "fec", "1", 0);
            ffmpeg.av_dict_set(&options, "packet_loss", "10", 0);

            FFmpegLoader.ThrowIfError(ffmpeg.avcodec_open2(_context, codec, &options), "opus avcodec_open2");
        }
        finally
        {
            if (options != null)
                ffmpeg.av_dict_free(&options);
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        if (_frame == null || _packet == null)
            throw new InvalidOperationException("Could not allocate Opus frame/packet.");

        _frame->nb_samples = FrameSizeSamples;
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
        ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_context->ch_layout);

        FFmpegLoader.ThrowIfError(ffmpeg.av_frame_get_buffer(_frame, 0), "opus av_frame_get_buffer");
    }

    /// <summary>
    /// Encodes exactly <see cref="FrameSizeSamples"/> interleaved stereo S16 samples.
    /// </summary>
    public void Encode(ReadOnlySpan<short> interleaved, EncodedPacketHandler onPacket)
    {
        lock (_gate)
        {
            if (_disposed || _context == null)
                return;

            var expected = FrameSizeSamples * Channels;
            if (interleaved.Length != expected)
                throw new ArgumentException(
                    $"Opus expects exactly {expected} interleaved samples (20ms stereo), got {interleaved.Length}.",
                    nameof(interleaved));

            FFmpegLoader.ThrowIfError(ffmpeg.av_frame_make_writable(_frame), "av_frame_make_writable");

            fixed (short* src = interleaved)
            {
                Buffer.MemoryCopy(src, _frame->data[0], expected * sizeof(short), expected * sizeof(short));
            }

            _frame->pts = _pts;
            _pts += FrameSizeSamples;

            var sendResult = ffmpeg.avcodec_send_frame(_context, _frame);
            if (sendResult < 0 && sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                FFmpegLoader.ThrowIfError(sendResult, "opus avcodec_send_frame");

            while (true)
            {
                var result = ffmpeg.avcodec_receive_packet(_context, _packet);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                    return;

                FFmpegLoader.ThrowIfError(result, "opus avcodec_receive_packet");

                try
                {
                    // The Opus RTP clock is 48 kHz, so pts is already the RTP timestamp.
                    onPacket(new ReadOnlySpan<byte>(_packet->data, _packet->size), (uint)_packet->pts, isKeyframe: true);
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
            if (_context != null) { var c = _context; ffmpeg.avcodec_free_context(&c); _context = null; }
        }
    }
}

/// <summary>Decodes Opus packets from a remote feed into interleaved stereo S16.</summary>
public sealed unsafe class OpusDecoder : IDisposable
{
    private readonly object _gate = new();
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private short[] _outputBuffer = new short[OpusEncoder.FrameSizeSamples * OpusEncoder.Channels];
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private AVSampleFormat _lastFormat = AVSampleFormat.AV_SAMPLE_FMT_NONE;
    private int _loggedFrameSizes;
    private bool _disposed;

    /// <summary>Packets the decoder refused. A non-zero rate here means lost audio.</summary>
    public long RejectedPackets { get; private set; }

    public long AcceptedPackets { get; private set; }

    private OpusDecoder() { }

    /// <summary>
    /// Opens a decoder for a stream with <paramref name="channels"/> channels. Defaults to
    /// mono, which is what WebRTC negotiates unless the SDP carries <c>stereo=1</c>.
    /// </summary>
    public static OpusDecoder Create(int channels = 1, ILogger? logger = null)
    {
        FFmpegLoader.EnsureAvailable();
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        var decoder = new OpusDecoder
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
        };
        try
        {
            decoder.Open(channels);
            (logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
                .LogDebug("Opus decoder open ({Channels}ch).", channels);
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    private void Open(int channels)
    {
        var codec = ffmpeg.avcodec_find_decoder_by_name("libopus");
        if (codec == null)
            codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_OPUS);

        if (codec == null)
            throw new InvalidOperationException("No Opus decoder in this FFmpeg build.");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context == null)
            throw new InvalidOperationException("Could not allocate an Opus decoder context.");

        _context->sample_rate = OpusEncoder.SampleRate;
        _context->request_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;

        // The channel count MUST match what the sender negotiated. RTP Opus carries no
        // extradata, so FFmpeg has nothing to correct a wrong value against: a decoder
        // configured for stereo, fed mono packets, silently produces half the samples per
        // frame. WebRTC negotiates mono unless the fmtp line says stereo=1.
        ffmpeg.av_channel_layout_default(&_context->ch_layout, channels);

        FFmpegLoader.ThrowIfError(ffmpeg.avcodec_open2(_context, codec, null), "opus decoder avcodec_open2");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        if (_frame == null || _packet == null)
            throw new InvalidOperationException("Could not allocate Opus decode frame/packet.");
    }

    /// <summary>Decodes one Opus packet, invoking <paramref name="onSamples"/> per output frame.</summary>
    public bool Decode(ReadOnlySpan<byte> packet, DecodedAudioHandler onSamples)
    {
        lock (_gate)
        {
            if (_disposed || _context == null || packet.IsEmpty)
                return false;

            fixed (byte* data = packet)
            {
                _packet->data = data;
                _packet->size = packet.Length;

                var sendResult = ffmpeg.avcodec_send_packet(_context, _packet);

                _packet->data = null;
                _packet->size = 0;

                if (sendResult < 0)
                {
                    // Previously swallowed. A steady rejection rate is indistinguishable
                    // from "the network is slow" once the samples are gone, so it has to
                    // be visible — but it must not log per packet at 50/s.
                    RejectedPackets++;

                    if (RejectedPackets == 1 || RejectedPackets % 250 == 0)
                    {
                        _logger.LogWarning(
                            "Opus decoder rejected {Count} packets ({Accepted} accepted). " +
                            "Last error: {Error}. First byte 0x{Toc:X2}, length {Length}.",
                            RejectedPackets, AcceptedPackets,
                            FFmpegLoader.DescribeError(sendResult),
                            packet[0], packet.Length);
                    }

                    return false;
                }

                AcceptedPackets++;
            }

            var produced = false;

            while (true)
            {
                var result = ffmpeg.avcodec_receive_frame(_context, _frame);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF || result < 0)
                    break;

                try
                {
                    EmitSamples(onSamples);
                    produced = true;
                }
                finally
                {
                    ffmpeg.av_frame_unref(_frame);
                }
            }

            return produced;
        }
    }

    private void EmitSamples(DecodedAudioHandler onSamples)
    {
        var channels = _frame->ch_layout.nb_channels;
        var samples = _frame->nb_samples;
        var total = samples * channels;

        if (_outputBuffer.Length < total)
            _outputBuffer = new short[total];

        var format = (AVSampleFormat)_frame->format;

        if (format != _lastFormat || _loggedFrameSizes < 3)
        {
            _lastFormat = format;
            _loggedFrameSizes++;
            _logger.LogInformation(
                "Opus decoded frame: {Format} {Channels}ch {SampleRate}Hz {Samples} samples/ch " +
                "({Duration:F1}ms).",
                format, channels, _frame->sample_rate, samples,
                samples * 1000.0 / Math.Max(_frame->sample_rate, 1));
        }

        // request_sample_fmt is only a hint. FFmpeg's native Opus decoder ignores it and
        // emits planar float, so the format has to be handled rather than assumed —
        // reading FLTP as S16 produces pure noise.
        fixed (short* dst = _outputBuffer)
        {
            switch (format)
            {
                case AVSampleFormat.AV_SAMPLE_FMT_S16:
                    Buffer.MemoryCopy(_frame->data[0], dst, total * sizeof(short), total * sizeof(short));
                    break;

                case AVSampleFormat.AV_SAMPLE_FMT_S16P:
                    InterleavePlanarS16(dst, samples, channels);
                    break;

                case AVSampleFormat.AV_SAMPLE_FMT_FLT:
                    ConvertInterleavedFloat(dst, total);
                    break;

                case AVSampleFormat.AV_SAMPLE_FMT_FLTP:
                    InterleavePlanarFloat(dst, samples, channels);
                    break;

                default:
                    _logger.LogWarning("Unsupported Opus decode format {Format}; dropping audio.", format);
                    return;
            }
        }

        onSamples(_outputBuffer.AsSpan(0, total), channels, _frame->sample_rate);
    }

    private void InterleavePlanarS16(short* destination, int samples, int channels)
    {
        for (var channel = 0; channel < channels; channel++)
        {
            var plane = (short*)_frame->data[(uint)channel];
            for (var i = 0; i < samples; i++)
                destination[i * channels + channel] = plane[i];
        }
    }

    private void ConvertInterleavedFloat(short* destination, int total)
    {
        var source = (float*)_frame->data[0];
        for (var i = 0; i < total; i++)
            destination[i] = ToPcm16(source[i]);
    }

    private void InterleavePlanarFloat(short* destination, int samples, int channels)
    {
        for (var channel = 0; channel < channels; channel++)
        {
            var plane = (float*)_frame->data[(uint)channel];
            for (var i = 0; i < samples; i++)
                destination[i * channels + channel] = ToPcm16(plane[i]);
        }
    }

    /// <summary>Clamps before scaling: decoded float can exceed ±1.0 and would wrap.</summary>
    private static short ToPcm16(float value)
        => (short)(Math.Clamp(value, -1.0f, 1.0f) * short.MaxValue);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
            if (_context != null) { var c = _context; ffmpeg.avcodec_free_context(&c); _context = null; }
        }
    }
}

/// <summary>Receives decoded interleaved PCM. The span is valid only during the call.</summary>
public delegate void DecodedAudioHandler(ReadOnlySpan<short> interleaved, int channels, int sampleRate);
