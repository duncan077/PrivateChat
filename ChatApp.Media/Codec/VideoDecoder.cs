using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ChatApp.Core.Media;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Codec;

/// <summary>
/// Decodes an Annex-B elementary stream from a remote feed into frames the renderer can
/// scale — I420 from a software decoder, NV12 when the GPU did the work.
/// <para>
/// Hardware decoding runs through an FFmpeg <b>device context</b> attached to the generic
/// decoder, never through a wrapper decoder such as <c>h264_cuvid</c>: the wrappers keep
/// an internal surface queue, and that queue is latency on every frame. With a device
/// context the loop stays synchronous, <see cref="ffmpeg.AV_CODEC_FLAG_LOW_DELAY"/> still
/// applies, and a GPU that cannot handle a particular stream falls back to software for
/// that stream alone.
/// </para>
/// <para>
/// Feeds arrive mid-stream, so the decoder is expected to reject data until the first
/// keyframe lands. Those early failures are normal and are swallowed rather than
/// surfaced — the caller's job is to request a PLI, not to log an error per packet.
/// </para>
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    /// <summary>
    /// Consecutive decode errors that end the hardware attempt. Surface exhaustion and a
    /// removed device both look like a run of failures with no pictures in between; one
    /// bad packet after loss looks the same for a frame or two, so the bar is a full
    /// second of nothing.
    /// </summary>
    private const int HardwareErrorLimit = 30;

    private readonly ILogger _logger;
    private readonly object _gate = new();

    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;

    // Hardware path: the device the decoder allocates surfaces from, the pixel format
    // those surfaces carry, and the frame the GPU reads back into.
    private AVBufferRef* _hardwareDevice;
    private AVPixelFormat _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVFrame* _download;
    private byte* _downloadBuffer;
    private Nv12Layout _downloadLayout;
    private int _downloadWidth;
    private int _downloadHeight;

    private VideoDecoderCandidate _candidate = new("none");
    private byte[] _outputBuffer = [];
    private AVPixelFormat _warnedFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private int _errorStreak;
    private bool _usingHardware;
    private bool _hardwareFailed;
    private bool _hasDecodedFrame;
    private bool _disposed;

    public VideoCodec Codec { get; }

    /// <summary>Active path, e.g. <c>h264+d3d11va</c>. Reflects what is really running.</summary>
    public string DecoderName { get; private set; } = string.Empty;

    /// <summary>True while frames are actually coming off the GPU.</summary>
    public bool IsHardware => _usingHardware;

    /// <summary>
    /// Set when hardware decoding broke mid-stream. The owner is expected to drop this
    /// decoder, build a software one, and ask the sender for a keyframe.
    /// </summary>
    public bool HardwareFailed => _hardwareFailed;

    public int Width => _context is null ? 0 : _context->width;
    public int Height => _context is null ? 0 : _context->height;

    /// <summary>False until the first frame decodes; the caller should keep asking for a keyframe.</summary>
    public bool HasDecodedFrame => _hasDecodedFrame;

    private VideoDecoder(VideoCodec codec, ILogger logger)
    {
        Codec = codec;
        _logger = logger;
    }

    public static VideoDecoder Create(VideoCodec codec, VideoEncoderPolicy policy, ILogger? logger = null)
    {
        FFmpegLoader.EnsureAvailable();

        var log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var decoder = new VideoDecoder(codec, log);
        var failures = new List<string>();

        foreach (var candidate in policy.GetDecoderCandidates(codec, CurrentPlatform()))
        {
            try
            {
                decoder.Open(candidate);
                log.LogInformation("Video decoder: {Decoder} for {Codec}.", decoder.DecoderName, codec);
                return decoder;
            }
            catch (Exception ex)
            {
                // A machine without the GPU, driver or profile for the first candidate is
                // the normal case, not an error: advance to the next one.
                failures.Add($"{candidate}: {ex.Message}");
                log.LogDebug(ex, "Decoder {Decoder} unavailable.", candidate.ToString());
                decoder.ReleaseResources();
            }
        }

        decoder.Dispose();
        throw new InvalidOperationException(
            $"No usable {codec} decoder. Tried:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", failures));
    }

    private void Open(VideoDecoderCandidate candidate)
    {
        var codec = ffmpeg.avcodec_find_decoder_by_name(candidate.DecoderName);
        if (codec == null)
            throw new InvalidOperationException($"decoder '{candidate.DecoderName}' not present");

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context == null)
            throw new InvalidOperationException("could not allocate a decoder context");

        // Low delay: never buffer frames waiting for reordering. Conferencing streams
        // have no B-frames, so there is nothing to reorder and holding frames only adds
        // latency.
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _context->thread_count = 0; // let FFmpeg pick
        _context->thread_type = ffmpeg.FF_THREAD_SLICE;

        if (candidate.IsHardware)
            AttachHardwareDevice(codec, candidate.Device);

        var result = ffmpeg.avcodec_open2(_context, codec, null);
        if (result < 0)
            throw new InvalidOperationException(FFmpegLoader.DescribeError(result));

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        if (_frame == null || _packet == null)
            throw new InvalidOperationException("could not allocate frame/packet");

        _candidate = candidate;
        DecoderName = candidate.ToString();
    }

    /// <summary>
    /// Points the decoder at a GPU: find the pixel format this decoder/device pair uses,
    /// open the device, and install the callback that claims that format when the decoder
    /// offers it.
    /// </summary>
    private void AttachHardwareDevice(AVCodec* codec, VideoHardwareDevice device)
    {
        var deviceType = ToDeviceType(device);
        var deviceName = ffmpeg.av_hwdevice_get_type_name(deviceType) ?? device.ToString();

        _hardwarePixelFormat = FindHardwarePixelFormat(codec, deviceType);
        if (_hardwarePixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            throw new InvalidOperationException($"this decoder has no {deviceName} configuration");

        AVBufferRef* hardwareDevice = null;
        var result = ffmpeg.av_hwdevice_ctx_create(&hardwareDevice, deviceType, null, null, 0);
        if (result < 0)
            throw new InvalidOperationException($"no {deviceName} device: {FFmpegLoader.DescribeError(result)}");

        _hardwareDevice = hardwareDevice;

        // libavcodec frees the reference it is handed, so it gets one of its own.
        _context->hw_device_ctx = ffmpeg.av_buffer_ref(_hardwareDevice);

        // The callback has to be static to be callable from native code, so the format it
        // should claim rides on the context's user pointer rather than a captured field.
        _context->opaque = (void*)(nint)(int)_hardwarePixelFormat;

        var selectFormat =
            (delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat>)&SelectPixelFormat;

        _context->get_format = new AVCodecContext_get_format_func { Pointer = (IntPtr)selectFormat };

        // Headroom in the surface pool: the decoder holds its reference frames and we hold
        // the one being read back. Running the pool dry stalls decoding outright.
        _context->extra_hw_frames = 3;

        _usingHardware = true;
    }

    /// <summary>
    /// The hardware pixel format this decoder advertises for a device type, or
    /// <c>AV_PIX_FMT_NONE</c> when the pair is not supported by this FFmpeg build.
    /// </summary>
    private static AVPixelFormat FindHardwarePixelFormat(AVCodec* codec, AVHWDeviceType deviceType)
    {
        for (var index = 0; ; index++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, index);
            if (config == null)
                return AVPixelFormat.AV_PIX_FMT_NONE;

            var viaDeviceContext =
                (config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0;

            if (config->device_type == deviceType && viaDeviceContext)
                return config->pix_fmt;
        }
    }

    /// <summary>
    /// Chooses the pixel format the decoder will produce. Called by FFmpeg once the
    /// stream's parameters are known — which is the first moment anyone can tell whether
    /// this GPU handles this profile.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AVPixelFormat SelectPixelFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        var wanted = (AVPixelFormat)(int)(nint)context->opaque;

        for (var format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
        {
            if (*format == wanted)
                return *format;
        }

        // The GPU will not take this stream — an unsupported profile or bit depth. FFmpeg
        // offers the software format alongside the hardware ones, so taking it here keeps
        // this one stream decoding on the CPU instead of failing the call.
        for (var format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
        {
            var descriptor = ffmpeg.av_pix_fmt_desc_get(*format);

            if (descriptor != null && (descriptor->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) == 0)
                return *format;
        }

        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    private static AVHWDeviceType ToDeviceType(VideoHardwareDevice device) => device switch
    {
        VideoHardwareDevice.D3d11Va => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
        VideoHardwareDevice.Vaapi => AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
        _ => AVHWDeviceType.AV_HWDEVICE_TYPE_NONE
    };

    private static OSPlatformKind CurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return OSPlatformKind.Windows;
        if (OperatingSystem.IsAndroid()) return OSPlatformKind.Android;
        return OSPlatformKind.Linux;
    }

    /// <summary>
    /// Decodes one Annex-B access unit, invoking <paramref name="onFrame"/> for each
    /// picture produced. Returns false if the data could not be decoded yet, which
    /// normally means we are still waiting for a keyframe.
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> annexB, DecodedFrameHandler onFrame)
    {
        lock (_gate)
        {
            if (_disposed || _context == null || annexB.IsEmpty)
                return false;

            // Dead pending replacement: the owner swaps in a software decoder as soon as
            // it sees the flag, and there is nothing to gain from failing again meanwhile.
            if (_hardwareFailed)
                return false;

            fixed (byte* data = annexB)
            {
                _packet->data = data;
                _packet->size = annexB.Length;

                var sendResult = ffmpeg.avcodec_send_packet(_context, _packet);

                _packet->data = null;
                _packet->size = 0;

                if (sendResult < 0)
                {
                    // Expected while waiting for the first keyframe.
                    if (!_hasDecodedFrame)
                        return false;

                    NoteDecodeError(sendResult, "avcodec_send_packet");
                    return false;
                }
            }

            var produced = false;

            while (true)
            {
                var result = ffmpeg.avcodec_receive_frame(_context, _frame);

                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                    break;

                if (result < 0)
                {
                    NoteDecodeError(result, "avcodec_receive_frame");
                    break;
                }

                try
                {
                    // The decoder produced a picture, so the reference chain is alive even
                    // if the format below turns out to be one we cannot hand on. That is
                    // what stops the caller asking for keyframes forever.
                    _hasDecodedFrame = true;

                    if (EmitFrame(onFrame))
                    {
                        produced = true;
                        _errorStreak = 0;
                    }
                }
                finally
                {
                    ffmpeg.av_frame_unref(_frame);
                }
            }

            return produced;
        }
    }

    private void NoteDecodeError(int errorCode, string operation)
    {
        _logger.LogDebug("{Operation}: {Error}", operation, FFmpegLoader.DescribeError(errorCode));

        if (!_usingHardware || _hardwareFailed)
            return;

        if (++_errorStreak < HardwareErrorLimit)
            return;

        _hardwareFailed = true;
        _logger.LogWarning(
            "{Decoder}: {Count} consecutive decode errors — abandoning hardware decode for this stream.",
            DecoderName, _errorStreak);
    }

    private bool EmitFrame(DecodedFrameHandler onFrame)
    {
        var format = (AVPixelFormat)_frame->format;

        if (_usingHardware && format == _hardwarePixelFormat)
            return EmitHardwareFrame(onFrame);

        if (_usingHardware)
        {
            // get_format handed back a software format: this driver refused the stream's
            // profile. Decoding still works, just on the CPU — worth saying once, because
            // the health log would otherwise keep claiming hardware.
            _usingHardware = false;
            DecoderName = $"{_candidate.DecoderName} (hardware declined)";

            _logger.LogInformation(
                "{Codec}: the GPU declined this stream's profile; decoding it in software.", Codec);
        }

        return EmitSoftwareFrame(onFrame, format);
    }

    /// <summary>
    /// Reads a decoded surface back into system memory. The destination is one contiguous,
    /// tightly-strided NV12 buffer, which is exactly the layout the renderer's scaler
    /// wants — so the read-back is the only copy on the way to the screen.
    /// </summary>
    private bool EmitHardwareFrame(DecodedFrameHandler onFrame)
    {
        var frames = _frame->hw_frames_ctx == null
            ? null
            : (AVHWFramesContext*)_frame->hw_frames_ctx->data;

        if (frames == null || frames->sw_format != AVPixelFormat.AV_PIX_FMT_NV12)
        {
            _logger.LogWarning(
                "{Decoder}: hardware surfaces are {Format}, and only NV12 read-back is implemented " +
                "— falling back to software for this stream.",
                DecoderName,
                frames == null ? "of an unknown format" : frames->sw_format.ToString());

            _hardwareFailed = true;
            return false;
        }

        try
        {
            EnsureDownloadFrame(_frame->width, _frame->height);
        }
        catch (Exception ex)
        {
            // Nothing here is worth killing the receive thread over: software decode needs
            // no read-back buffer at all, so failing over is a strictly better answer than
            // propagating.
            _logger.LogWarning(ex, "{Decoder}: could not prepare the read-back buffer.", DecoderName);
            _hardwareFailed = true;
            return false;
        }

        var result = ffmpeg.av_hwframe_transfer_data(_download, _frame, 0);
        if (result < 0)
        {
            _logger.LogWarning(
                "{Decoder}: could not read the decoded surface back ({Error}) — falling back to software.",
                DecoderName, FFmpegLoader.DescribeError(result));

            _hardwareFailed = true;
            return false;
        }

        Span<int> offsets = [0, _downloadLayout.LumaSize];
        Span<int> strides = [_downloadLayout.LumaStride, _downloadLayout.ChromaStride];

        Emit(
            onFrame,
            VideoPixelFormat.Nv12,
            new ReadOnlySpan<byte>(_downloadBuffer, _downloadLayout.Total),
            offsets,
            strides,
            _downloadWidth,
            _downloadHeight);

        return true;
    }

    /// <summary>
    /// Packs the decoder's stride-aligned planes into a contiguous buffer the renderer can
    /// scale directly. Software decoders allocate each plane from its own pool, so there
    /// is no contiguous view to hand on without this pass.
    /// </summary>
    private bool EmitSoftwareFrame(DecodedFrameHandler onFrame, AVPixelFormat format)
    {
        var width = _frame->width;
        var height = _frame->height;

        switch (format)
        {
            // YUVJ420P is the same layout, tagged full-range.
            case AVPixelFormat.AV_PIX_FMT_YUV420P:
            case AVPixelFormat.AV_PIX_FMT_YUVJ420P:
            {
                var lumaSize = width * height;
                var chromaWidth = (width + 1) / 2;
                var chromaHeight = (height + 1) / 2;
                var chromaSize = chromaWidth * chromaHeight;
                var total = lumaSize + chromaSize * 2;

                EnsureOutputBuffer(total);

                fixed (byte* dst = _outputBuffer)
                {
                    CopyPlane(_frame->data[0], _frame->linesize[0], dst, width, width, height);
                    CopyPlane(_frame->data[1], _frame->linesize[1], dst + lumaSize, chromaWidth, chromaWidth, chromaHeight);
                    CopyPlane(_frame->data[2], _frame->linesize[2], dst + lumaSize + chromaSize, chromaWidth, chromaWidth, chromaHeight);
                }

                Span<int> offsets = [0, lumaSize, lumaSize + chromaSize];
                Span<int> strides = [width, chromaWidth, chromaWidth];

                Emit(onFrame, VideoPixelFormat.I420, _outputBuffer.AsSpan(0, total), offsets, strides, width, height);
                return true;
            }

            // MediaCodec, and some drivers on the software fallback path, hand back NV12.
            case AVPixelFormat.AV_PIX_FMT_NV12:
            {
                var layout = Nv12Layout.For(width, height);

                EnsureOutputBuffer(layout.Total);

                fixed (byte* dst = _outputBuffer)
                {
                    CopyPlane(
                        _frame->data[0], _frame->linesize[0],
                        dst, layout.LumaStride, width, height);

                    CopyPlane(
                        _frame->data[1], _frame->linesize[1],
                        dst + layout.LumaSize, layout.ChromaStride, layout.ChromaStride, layout.ChromaHeight);
                }

                Span<int> offsets = [0, layout.LumaSize];
                Span<int> strides = [layout.LumaStride, layout.ChromaStride];

                Emit(
                    onFrame, VideoPixelFormat.Nv12, _outputBuffer.AsSpan(0, layout.Total),
                    offsets, strides, width, height);

                return true;
            }

            default:
                WarnUnsupportedFormat(format);
                return false;
        }
    }

    private static void Emit(
        DecodedFrameHandler onFrame,
        VideoPixelFormat format,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<int> offsets,
        ReadOnlySpan<int> strides,
        int width,
        int height)
    {
        var frame = new VideoFrame
        {
            Width = width,
            Height = height,
            Format = format,
            Data = data,
            PlaneOffsets = offsets,
            PlaneStrides = strides,
            RtpTimestamp = 0
        };

        onFrame(in frame);
    }

    /// <summary>
    /// Prepares the read-back destination. <c>av_hwframe_transfer_data</c> only writes into
    /// a frame that already owns its memory — given a null buffer it allocates its own,
    /// one allocation per plane — so the buffer is wrapped in an AVBufferRef and the plane
    /// pointers are laid out by hand.
    /// </summary>
    private void EnsureDownloadFrame(int width, int height)
    {
        if (_download != null && _downloadWidth == width && _downloadHeight == height)
            return;

        ReleaseDownloadFrame();

        var layout = Nv12Layout.For(width, height);

        var buffer = (byte*)ffmpeg.av_malloc((ulong)layout.Total);
        if (buffer == null)
            throw new InvalidOperationException("could not allocate a frame read-back buffer");

        // A null free callback means av_free, which matches the av_malloc above.
        var reference = ffmpeg.av_buffer_create(buffer, (ulong)layout.Total, default, null, 0);
        if (reference == null)
        {
            ffmpeg.av_free(buffer);
            throw new InvalidOperationException("could not wrap the frame read-back buffer");
        }

        _download = ffmpeg.av_frame_alloc();
        if (_download == null)
        {
            ffmpeg.av_buffer_unref(&reference);
            throw new InvalidOperationException("could not allocate a read-back frame");
        }

        _download->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        _download->width = width;
        _download->height = height;
        _download->buf[0] = reference;
        _download->data[0] = buffer;
        _download->data[1] = buffer + layout.LumaSize;
        _download->linesize[0] = layout.LumaStride;
        _download->linesize[1] = layout.ChromaStride;

        _downloadBuffer = buffer;
        _downloadLayout = layout;
        _downloadWidth = width;
        _downloadHeight = height;
    }

    private void EnsureOutputBuffer(int size)
    {
        if (_outputBuffer.Length < size)
            _outputBuffer = new byte[size];
    }

    private void WarnUnsupportedFormat(AVPixelFormat format)
    {
        if (_warnedFormat == format)
            return;

        _warnedFormat = format;
        _logger.LogWarning(
            "{Decoder} produced {Format} frames; only I420 and NV12 reach the renderer, so these are dropped.",
            DecoderName, format);
    }

    private static void CopyPlane(byte* src, int srcStride, byte* dst, int dstStride, int width, int height)
    {
        for (var y = 0; y < height; y++)
            Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, dstStride, width);
    }

    private void ReleaseDownloadFrame()
    {
        if (_download != null)
        {
            // Frees buf[0], and with it the buffer av_malloc handed out.
            var download = _download;
            ffmpeg.av_frame_free(&download);
            _download = null;
        }

        _downloadBuffer = null;
        _downloadLayout = default;
        _downloadWidth = 0;
        _downloadHeight = 0;
    }

    private void ReleaseResources()
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
            // Releases the context's own reference to the hardware device.
            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
        }

        if (_hardwareDevice != null)
        {
            var device = _hardwareDevice;
            ffmpeg.av_buffer_unref(&device);
            _hardwareDevice = null;
        }

        ReleaseDownloadFrame();

        // Reset so the next candidate in the ladder starts from a clean state.
        _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _usingHardware = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseResources();
        }
    }
}

/// <summary>
/// Byte layout of a tightly-packed NV12 frame.
/// <para>
/// The interleaved chroma row is <c>2 * ceil(width / 2)</c> bytes, which is one byte
/// <b>wider</b> than the luma row at odd widths — one UV pair still covers two luma
/// columns when there is only one column left. FFmpeg checks the destination stride
/// against that width with <c>av_assert0</c>, an assertion compiled into release builds
/// that calls <c>abort()</c>, so getting this wrong is a hard process kill on the first
/// odd-width frame rather than a wrong pixel. H.264 and HEVC can only signal even widths
/// for 4:2:0, but AV1 and VP9 can signal any width at all.
/// </para>
/// </summary>
internal readonly record struct Nv12Layout(
    int LumaStride,
    int ChromaStride,
    int ChromaHeight,
    int LumaSize,
    int Total)
{
    public static Nv12Layout For(int width, int height)
    {
        var chromaStride = width + (width & 1);
        var chromaHeight = (height + 1) / 2;
        var lumaSize = width * height;

        return new Nv12Layout(
            LumaStride: width,
            ChromaStride: chromaStride,
            ChromaHeight: chromaHeight,
            LumaSize: lumaSize,
            Total: lumaSize + chromaStride * chromaHeight);
    }
}

/// <summary>
/// Receives one decoded picture. The frame's memory is only valid during the call.
/// A custom delegate because <c>Action&lt;&gt;</c> cannot carry a ref struct.
/// </summary>
public delegate void DecodedFrameHandler(in VideoFrame frame);
