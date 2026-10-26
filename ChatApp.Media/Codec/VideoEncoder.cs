using System.Runtime.InteropServices;
using ChatApp.Core.Media;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Codec;

/// <summary>
/// Encodes raw I420 frames into an Annex-B elementary stream for libdatachannel's RTP
/// packetizer.
/// <para>
/// Tuned for real-time conferencing rather than file output: zero-latency (no B-frames,
/// no lookahead), CBR-ish rate control, and an effectively infinite GOP because
/// keyframes are driven on demand by RTCP PLI instead of on a fixed interval. A periodic
/// keyframe would waste bandwidth for every participant who did not need one.
/// </para>
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    /// <summary>RTP video clock. Timestamps handed to the packetizer use this rate.</summary>
    public const int ClockRate = 90_000;

    private readonly ILogger _logger;
    private readonly object _gate = new();

    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _scaler;

    private int _sourceWidth;
    private int _sourceHeight;
    private AVPixelFormat _sourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;

    // sws_scale takes managed pointer arrays. These are reused rather than allocated
    // per frame — at 30–60fps the churn would be pointless GC pressure.
    private readonly byte*[] _scaleSrcData = new byte*[4];
    private readonly int[] _scaleSrcStride = new int[4];
    private readonly byte*[] _scaleDstData = new byte*[4];
    private readonly int[] _scaleDstStride = new int[4];

    private long _frameIndex;
    private int _forceKeyframe;
    private bool _disposed;

    public string EncoderName { get; private set; } = string.Empty;
    public VideoCodec Codec { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameRate { get; }

    /// <summary>True when the selected encoder runs on the GPU.</summary>
    public bool IsHardware { get; private set; }

    private VideoEncoder(VideoCodec codec, int width, int height, int frameRate, ILogger logger)
    {
        Codec = codec;
        // H.264 and HEVC require even dimensions for 4:2:0 chroma.
        Width = width & ~1;
        Height = height & ~1;
        FrameRate = frameRate;
        _logger = logger;
    }

    /// <summary>
    /// Opens the first encoder from <paramref name="policy"/> that this machine actually
    /// supports, falling back through the candidate list to software.
    /// </summary>
    public static VideoEncoder Create(
        VideoCodec codec,
        int width,
        int height,
        int frameRate,
        int bitrateKbps,
        VideoEncoderPolicy policy,
        VideoContentHint contentHint = VideoContentHint.Motion,
        ILogger? logger = null)
    {
        FFmpegLoader.EnsureAvailable();

        var log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var encoder = new VideoEncoder(codec, width, height, frameRate, log);

        var candidates = policy.GetEncoderCandidates(codec, CurrentPlatform());
        var failures = new List<string>();

        foreach (var name in candidates)
        {
            try
            {
                encoder.Open(name, bitrateKbps, contentHint);
                log.LogInformation(
                    "Video encoder: {Encoder} ({Codec} {Width}x{Height}@{Fps} {Bitrate}kbps, hardware={IsHardware}).",
                    name, codec, encoder.Width, encoder.Height, frameRate, bitrateKbps, encoder.IsHardware);
                return encoder;
            }
            catch (Exception ex)
            {
                // Hardware encoders are routinely absent (no NVIDIA GPU, no QSV). Trying
                // the next candidate is the normal path, not an error.
                failures.Add($"{name}: {ex.Message}");
                encoder.ReleaseCodecResources();
            }
        }

        encoder.Dispose();
        throw new InvalidOperationException(
            $"No usable {codec} encoder. Tried:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", failures));
    }

    private void Open(string encoderName, int bitrateKbps, VideoContentHint contentHint)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        if (codec == null)
            throw new InvalidOperationException("not present in this FFmpeg build");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context == null)
            throw new InvalidOperationException("could not allocate a codec context");

        var bitrate = (long)bitrateKbps * 1000;

        _context->width = Width;
        _context->height = Height;
        _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _context->time_base = new AVRational { num = 1, den = FrameRate };
        _context->framerate = new AVRational { num = FrameRate, den = 1 };

        _context->bit_rate = bitrate;
        _context->rc_max_rate = bitrate;
        // One second of buffering: large enough to absorb a complex frame, small enough
        // that the encoder cannot bank credit and then burst past the link capacity.
        _context->rc_buffer_size = (int)bitrate;

        // No B-frames: they add at least one frame of latency and Janus would have to
        // reorder them.
        _context->max_b_frames = 0;

        // Keyframes come from PLI, not a timer.
        _context->gop_size = int.MaxValue;

        ApplyContextTuning(contentHint);

        AVDictionary* options = null;
        try
        {
            BuildPrivateOptions(encoderName, contentHint, &options);

            var result = ffmpeg.avcodec_open2(_context, codec, &options);
            if (result < 0)
                throw new InvalidOperationException(FFmpegLoader.DescribeError(result));
        }
        finally
        {
            if (options != null)
                ffmpeg.av_dict_free(&options);
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        if (_frame == null || _packet == null)
            throw new InvalidOperationException("could not allocate frame/packet");

        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        _frame->width = Width;
        _frame->height = Height;

        FFmpegLoader.ThrowIfError(ffmpeg.av_frame_get_buffer(_frame, 32), "av_frame_get_buffer");

        EncoderName = encoderName;
        IsHardware = !encoderName.StartsWith("lib", StringComparison.Ordinal);
    }

    private void ApplyContextTuning(VideoContentHint contentHint)
    {
        // AV_CODEC_FLAG_GLOBAL_HEADER is deliberately NOT set. It moves SPS/PPS out of
        // the stream and into extradata, which suits containers but breaks us: Janus
        // forwards RTP without rewriting parameter sets, so a participant joining later
        // would never receive them and could not start decoding. In-band repetition on
        // every keyframe (the repeat-headers options below) is what we want instead.

        // Screen and text content is mostly static with sharp edges, so cap how coarse
        // the quantizer may get; motion content tolerates more spatial loss in exchange
        // for holding the frame rate. This is the native equivalent of the browser's
        // video contentHint.
        if (contentHint is VideoContentHint.Detail or VideoContentHint.Text)
            _context->qmin = 10;
    }

    private void BuildPrivateOptions(string encoderName, VideoContentHint contentHint, AVDictionary** options)
    {
        void Set(string key, string value) => ffmpeg.av_dict_set(options, key, value, 0);

        switch (encoderName)
        {
            case "libx264":
                Set("preset", "veryfast");
                Set("tune", contentHint is VideoContentHint.Text or VideoContentHint.Detail
                    ? "stillimage"
                    : "zerolatency");
                // Repeat parameter sets on every keyframe for late joiners.
                Set("x264-params", "repeat-headers=1:sliced-threads=1");

                // Main, to match what is advertised in the SDP and what the hardware
                // encoders below produce. A receiver configures its decoder from the
                // advertised profile, so the encoders must not each pick their own.
                Set("profile", "main");
                break;

            case "libx265":
                Set("preset", "veryfast");
                Set("tune", "zerolatency");
                Set("x265-params", "repeat-headers=1");
                break;

            case "libsvtav1":
                Set("preset", "10");
                Set("svtav1-params", "lp=2:pred-struct=1");
                break;

            case "h264_nvenc":
                // Pinned rather than left to the default, for the same reason as libx264:
                // the SDP promises Main and the stream has to be Main.
                Set("profile", "main");
                goto case "hevc_nvenc";

            case "hevc_nvenc":
            case "av1_nvenc":
                Set("preset", "p1");
                Set("tune", "ull");          // ultra-low latency
                // VBR, not CBR: call content is bursty in exactly the way CBR is bad at —
                // a screen share sits still, then changes a lot for a few frames. CBR pads
                // the still frames and refuses to spend extra on the busy ones, meeting a
                // burst by degrading the frame. VBR under the same rc_max_rate keeps the
                // ceiling while placing the bits where the content needs them.
                Set("rc", "vbr");
                Set("zerolatency", "1");
                Set("repeat_spspps", "1");
                break;

            case "h264_qsv":
                Set("profile", "main");
                goto case "hevc_qsv";

            case "hevc_qsv":
            case "av1_qsv":
                Set("preset", "veryfast");
                Set("low_power", "1");
                break;

            case "h264_amf":
                Set("profile", "main");
                goto case "hevc_amf";

            case "hevc_amf":
            case "av1_amf":
                Set("usage", "ultralowlatency");
                // Same reasoning as nvenc: latency-constrained VBR instead of CBR.
                Set("rc", "vbr_latency");
                break;

            case "h264_mediacodec":
            case "hevc_mediacodec":
            case "av1_mediacodec":
                Set("ndk_codec", "1");
                break;

            case "h264_vaapi":
            case "hevc_vaapi":
            case "av1_vaapi":
                Set("rc_mode", "CBR");
                break;
        }
    }

    /// <summary>
    /// Forces the next encoded frame to be an IDR. Called from the RTCP PLI handler, so
    /// it must be safe to invoke from libdatachannel's thread.
    /// </summary>
    public void RequestKeyframe() => Interlocked.Exchange(ref _forceKeyframe, 1);

    /// <summary>
    /// Retunes the target bitrate in place, without reopening the encoder.
    /// Driven by RTCP REMB from Janus.
    /// </summary>
    public void SetBitrate(int bitrateKbps)
    {
        lock (_gate)
        {
            if (_disposed || _context == null)
                return;

            var bitrate = (long)bitrateKbps * 1000;
            _context->bit_rate = bitrate;
            _context->rc_max_rate = bitrate;
            _context->rc_buffer_size = (int)bitrate;
        }
    }

    /// <summary>
    /// Encodes one frame and invokes <paramref name="onPacket"/> for each Annex-B packet
    /// produced, along with its 90 kHz RTP timestamp.
    /// <para>
    /// <paramref name="source"/> is converted to I420 at the encoder's resolution when it
    /// does not already match.
    /// </para>
    /// </summary>
    public void Encode(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        VideoPixelFormat sourceFormat,
        EncodedPacketHandler onPacket)
    {
        lock (_gate)
        {
            if (_disposed || _context == null)
                return;

            FFmpegLoader.ThrowIfError(ffmpeg.av_frame_make_writable(_frame), "av_frame_make_writable");

            FillFrame(source, sourceWidth, sourceHeight, sourceFormat);

            // Presentation timestamps run on the codec time base (1/fps); the RTP
            // timestamp is derived separately on the 90 kHz clock.
            _frame->pts = _frameIndex;

            if (Interlocked.Exchange(ref _forceKeyframe, 0) == 1)
            {
                _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                // flags must be cleared or FFmpeg keeps the previous frame's decision.
                _frame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
            }
            else
            {
                _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                _frame->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;
            }

            var sendResult = ffmpeg.avcodec_send_frame(_context, _frame);
            if (sendResult < 0 && sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                FFmpegLoader.ThrowIfError(sendResult, "avcodec_send_frame");

            DrainPackets(onPacket);

            _frameIndex++;
        }
    }

    private void DrainPackets(EncodedPacketHandler onPacket)
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(_context, _packet);

            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                return;

            FFmpegLoader.ThrowIfError(result, "avcodec_receive_packet");

            try
            {
                var payload = new ReadOnlySpan<byte>(_packet->data, _packet->size);

                // Convert the codec time base to the 90 kHz RTP clock.
                var rtpTimestamp = (uint)(_packet->pts * ClockRate / FrameRate);
                var isKeyframe = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                onPacket(payload, rtpTimestamp, isKeyframe);
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

    private void FillFrame(ReadOnlySpan<byte> source, int width, int height, VideoPixelFormat format)
    {
        var avFormat = ToAvPixelFormat(format);

        if (width == Width && height == Height && avFormat == AVPixelFormat.AV_PIX_FMT_YUV420P)
        {
            CopyI420Planes(source);
            return;
        }

        EnsureScaler(width, height, avFormat);
        ScaleIntoFrame(source, width, height, avFormat);
    }

    private void CopyI420Planes(ReadOnlySpan<byte> source)
    {
        var lumaSize = Width * Height;
        var chromaWidth = Width / 2;
        var chromaHeight = Height / 2;
        var chromaSize = chromaWidth * chromaHeight;

        if (source.Length < lumaSize + chromaSize * 2)
            throw new ArgumentException(
                $"I420 buffer too small: expected {lumaSize + chromaSize * 2} bytes for " +
                $"{Width}x{Height}, got {source.Length}.", nameof(source));

        // Copy row by row: the AVFrame's stride is aligned and usually wider than the
        // packed source, so a single memcpy would shear the image.
        fixed (byte* src = source)
        {
            CopyPlane(src, Width, _frame->data[0], _frame->linesize[0], Width, Height);
            CopyPlane(src + lumaSize, chromaWidth, _frame->data[1], _frame->linesize[1], chromaWidth, chromaHeight);
            CopyPlane(src + lumaSize + chromaSize, chromaWidth, _frame->data[2], _frame->linesize[2], chromaWidth, chromaHeight);
        }
    }

    private static void CopyPlane(byte* src, int srcStride, byte* dst, int dstStride, int width, int height)
    {
        for (var y = 0; y < height; y++)
            Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, dstStride, width);
    }

    private void EnsureScaler(int width, int height, AVPixelFormat format)
    {
        if (_scaler != null && _sourceWidth == width && _sourceHeight == height && _sourceFormat == format)
            return;

        if (_scaler != null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        _scaler = ffmpeg.sws_getContext(
            width, height, format,
            Width, Height, AVPixelFormat.AV_PIX_FMT_YUV420P,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);

        if (_scaler == null)
            throw new InvalidOperationException(
                $"Could not create a scaler from {width}x{height} {format} to {Width}x{Height} yuv420p.");

        _sourceWidth = width;
        _sourceHeight = height;
        _sourceFormat = format;
    }

    private void ScaleIntoFrame(ReadOnlySpan<byte> source, int width, int height, AVPixelFormat format)
    {
        Array.Clear(_scaleSrcData);
        Array.Clear(_scaleSrcStride);

        fixed (byte* src = source)
        {
            switch (format)
            {
                case AVPixelFormat.AV_PIX_FMT_BGRA:
                    _scaleSrcData[0] = src;
                    _scaleSrcStride[0] = width * 4;
                    break;

                case AVPixelFormat.AV_PIX_FMT_NV12:
                    _scaleSrcData[0] = src;
                    _scaleSrcData[1] = src + (long)width * height;
                    _scaleSrcStride[0] = width;
                    _scaleSrcStride[1] = width;
                    break;

                default: // I420
                    var lumaSize = (long)width * height;
                    var chromaSize = (long)(width / 2) * (height / 2);
                    _scaleSrcData[0] = src;
                    _scaleSrcData[1] = src + lumaSize;
                    _scaleSrcData[2] = src + lumaSize + chromaSize;
                    _scaleSrcStride[0] = width;
                    _scaleSrcStride[1] = width / 2;
                    _scaleSrcStride[2] = width / 2;
                    break;
            }

            for (uint i = 0; i < 4; i++)
            {
                _scaleDstData[i] = _frame->data[i];
                _scaleDstStride[i] = _frame->linesize[i];
            }

            ffmpeg.sws_scale(_scaler, _scaleSrcData, _scaleSrcStride, 0, height, _scaleDstData, _scaleDstStride);
        }
    }

    private static AVPixelFormat ToAvPixelFormat(VideoPixelFormat format) => format switch
    {
        VideoPixelFormat.Nv12 => AVPixelFormat.AV_PIX_FMT_NV12,
        VideoPixelFormat.Bgra32 => AVPixelFormat.AV_PIX_FMT_BGRA,
        _ => AVPixelFormat.AV_PIX_FMT_YUV420P
    };

    private static OSPlatformKind CurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return OSPlatformKind.Windows;
        if (OperatingSystem.IsAndroid()) return OSPlatformKind.Android;
        return OSPlatformKind.Linux;
    }

    private void ReleaseCodecResources()
    {
        if (_frame != null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_packet != null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_context != null)
        {
            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
        }

        if (_scaler != null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseCodecResources();
        }
    }
}

/// <summary>
/// Receives one encoded packet. The span is only valid for the duration of the call.
/// A custom delegate because <c>Action&lt;&gt;</c> cannot carry a <c>ReadOnlySpan</c>.
/// </summary>
public delegate void EncodedPacketHandler(ReadOnlySpan<byte> payload, uint rtpTimestamp, bool isKeyframe);
