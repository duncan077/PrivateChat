using ChatApp.Core.Media;
using ChatApp.Media.Codec;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Capture;

/// <summary>
/// Pulls video from any FFmpeg input device and delivers I420 frames.
/// <para>
/// Shared by camera capture (<c>dshow</c> / <c>v4l2</c>) and screen capture
/// (<c>ddagrab</c> via the <c>lavfi</c> device / <c>x11grab</c>). FFmpeg's device layer
/// is pull-based, so a dedicated thread runs the read loop; capture backends elsewhere
/// in this app are push-based and need no thread of their own.
/// </para>
/// <para>
/// Frames are converted to I420 here rather than in the encoder: it is the format the
/// encoder wants natively, so doing it once at the source avoids a second conversion of
/// every frame.
/// </para>
/// </summary>
internal sealed unsafe class FFmpegCaptureSource : IDisposable
{
    private static int _devicesRegistered;

    private readonly ILogger _logger;
    private readonly object _gate = new();

    private AVFormatContext* _format;
    private AVCodecContext* _decoder;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _scaler;
    private int _streamIndex = -1;

    private byte[] _output = [];
    private readonly byte*[] _dstData = new byte*[4];
    private readonly int[] _dstStride = new int[4];
    private readonly byte*[] _srcData = new byte*[4];
    private readonly int[] _srcStride = new int[4];

    private Thread? _readThread;
    private CancellationTokenSource? _cts;
    private AVPixelFormat _lastSourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private int _lastSourceWidth;
    private int _lastSourceHeight;
    private bool _disposed;
    private bool _abandoned;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsCapturing { get; private set; }

    public event VideoCapturedHandler? FrameCaptured;
    public event Action<string>? CaptureFailed;

    public FFmpegCaptureSource(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>Registers FFmpeg's device inputs. Safe to call repeatedly.</summary>
    public static void EnsureDevicesRegistered()
    {
        FFmpegLoader.EnsureAvailable();

        if (Interlocked.Exchange(ref _devicesRegistered, 1) == 1)
            return;

        ffmpeg.avdevice_register_all();
    }

    /// <summary>
    /// Opens <paramref name="url"/> on the named input format and starts delivering
    /// frames.
    /// </summary>
    public void Start(string inputFormatName, string url, IReadOnlyDictionary<string, string> options)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_abandoned)
                throw new InvalidOperationException(
                    "This capture source was abandoned with a thread still running in it and cannot be reused.");

            if (IsCapturing)
                return;

            EnsureDevicesRegistered();

            try
            {
                OpenInput(inputFormatName, url, options);
                OpenDecoder();

                _cts = new CancellationTokenSource();
                _readThread = new Thread(() => ReadLoop(_cts.Token))
                {
                    IsBackground = true,
                    Name = $"ffmpeg-capture-{inputFormatName}",
                    // Dropped frames are visible to every participant, so this thread
                    // gets priority over ordinary background work.
                    Priority = ThreadPriority.AboveNormal
                };

                IsCapturing = true;
                _readThread.Start();

                _logger.LogInformation(
                    "Capture started: {Format} '{Url}' at {Width}x{Height}.",
                    inputFormatName, url, Width, Height);
            }
            catch
            {
                ReleaseUnsafe();
                throw;
            }
        }
    }

    private void OpenInput(string inputFormatName, string url, IReadOnlyDictionary<string, string> options)
    {
        var inputFormat = ffmpeg.av_find_input_format(inputFormatName);
        if (inputFormat == null)
            throw new InvalidOperationException(
                $"FFmpeg input '{inputFormatName}' is unavailable. " +
                "The avdevice library may be missing — run tools/fetch-ffmpeg.ps1.");

        AVDictionary* dictionary = null;
        AVFormatContext* format = null;

        try
        {
            foreach (var (key, value) in options)
                ffmpeg.av_dict_set(&dictionary, key, value, 0);

            var result = ffmpeg.avformat_open_input(&format, url, inputFormat, &dictionary);
            if (result < 0)
                throw new InvalidOperationException(
                    $"Could not open '{url}' via {inputFormatName}: {FFmpegLoader.DescribeError(result)}");

            _format = format;
            format = null;

            FFmpegLoader.ThrowIfError(
                ffmpeg.avformat_find_stream_info(_format, null), "avformat_find_stream_info");

            _streamIndex = ffmpeg.av_find_best_stream(
                _format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);

            if (_streamIndex < 0)
                throw new InvalidOperationException($"No video stream from '{url}'.");
        }
        finally
        {
            if (dictionary != null)
                ffmpeg.av_dict_free(&dictionary);

            if (format != null)
                ffmpeg.avformat_close_input(&format);
        }
    }

    private void OpenDecoder()
    {
        var stream = _format->streams[_streamIndex];
        var codecParameters = stream->codecpar;

        // Devices may hand back raw frames (rawvideo/bgra) or a compressed stream —
        // most USB cameras default to MJPEG at higher resolutions — so decode either way.
        var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
        if (codec == null)
            throw new InvalidOperationException($"No decoder for capture codec {codecParameters->codec_id}.");

        _decoder = ffmpeg.avcodec_alloc_context3(codec);
        if (_decoder == null)
            throw new InvalidOperationException("Could not allocate a capture decoder context.");

        FFmpegLoader.ThrowIfError(
            ffmpeg.avcodec_parameters_to_context(_decoder, codecParameters), "avcodec_parameters_to_context");

        _decoder->thread_count = 0;
        FFmpegLoader.ThrowIfError(ffmpeg.avcodec_open2(_decoder, codec, null), "capture avcodec_open2");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        if (_frame == null || _packet == null)
            throw new InvalidOperationException("Could not allocate capture frame/packet.");

        Width = codecParameters->width;
        Height = codecParameters->height;
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = ffmpeg.av_read_frame(_format, _packet);

                if (result == ffmpeg.AVERROR_EOF)
                {
                    Fail("The capture device stopped producing frames.");
                    return;
                }

                if (result < 0)
                {
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        // Device has nothing ready yet. A short sleep avoids burning a
                        // core on a spin loop.
                        Thread.Sleep(1);
                        continue;
                    }

                    Fail($"Capture read failed: {FFmpegLoader.DescribeError(result)}");
                    return;
                }

                try
                {
                    if (_packet->stream_index == _streamIndex)
                        DecodePacket(cancellationToken);
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture loop terminated unexpectedly.");
            Fail(ex.Message);
        }
    }

    private void DecodePacket(CancellationToken cancellationToken)
    {
        if (ffmpeg.avcodec_send_packet(_decoder, _packet) < 0)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = ffmpeg.avcodec_receive_frame(_decoder, _frame);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF || result < 0)
                return;

            try
            {
                EmitFrame();
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    private void EmitFrame()
    {
        var handler = FrameCaptured;
        if (handler is null)
            return;

        var width = _frame->width;
        var height = _frame->height;
        var sourceFormat = (AVPixelFormat)_frame->format;

        if (width <= 0 || height <= 0)
            return;

        Width = width;
        Height = height;

        EnsureScaler(width, height, sourceFormat);

        var lumaSize = width * height;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var chromaSize = chromaWidth * chromaHeight;
        var total = lumaSize + chromaSize * 2;

        if (_output.Length < total)
            _output = new byte[total];

        fixed (byte* dst = _output)
        {
            _dstData[0] = dst;
            _dstData[1] = dst + lumaSize;
            _dstData[2] = dst + lumaSize + chromaSize;
            _dstData[3] = null;
            _dstStride[0] = width;
            _dstStride[1] = chromaWidth;
            _dstStride[2] = chromaWidth;
            _dstStride[3] = 0;

            for (uint i = 0; i < 4; i++)
            {
                _srcData[i] = _frame->data[i];
                _srcStride[i] = _frame->linesize[i];
            }

            ffmpeg.sws_scale(_scaler, _srcData, _srcStride, 0, height, _dstData, _dstStride);
        }

        handler(_output.AsSpan(0, total), width, height, VideoPixelFormat.I420);
    }

    private void EnsureScaler(int width, int height, AVPixelFormat sourceFormat)
    {
        if (_scaler != null
            && _lastSourceWidth == width
            && _lastSourceHeight == height
            && _lastSourceFormat == sourceFormat)
        {
            return;
        }

        if (_scaler != null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        _scaler = ffmpeg.sws_getContext(
            width, height, sourceFormat,
            width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);

        if (_scaler == null)
            throw new InvalidOperationException($"Could not convert {sourceFormat} to yuv420p.");

        _lastSourceWidth = width;
        _lastSourceHeight = height;
        _lastSourceFormat = sourceFormat;

        _logger.LogDebug("Capture pixel format {Format} at {Width}x{Height}.", sourceFormat, width, height);
    }

    private void Fail(string message)
    {
        IsCapturing = false;
        _logger.LogError("Capture failed: {Message}", message);
        CaptureFailed?.Invoke(message);
    }

    public void Stop()
    {
        Thread? thread;

        lock (_gate)
        {
            if (!IsCapturing && _readThread is null)
                return;

            IsCapturing = false;
            _cts?.Cancel();
            thread = _readThread;
            _readThread = null;
        }

        // Joined outside the lock: the read thread can be blocked inside av_read_frame,
        // and holding the lock while waiting would deadlock a concurrent Stop.
        var stopped = thread is null || thread.Join(TimeSpan.FromSeconds(3));

        lock (_gate)
        {
            if (stopped)
                ReleaseUnsafe();
            else
                AbandonUnsafe();
        }
    }

    /// <summary>
    /// Walks away from a read thread that will not stop, leaving everything it is using
    /// allocated.
    /// <para>
    /// The thread is somewhere inside FFmpeg holding the format context and the packet.
    /// Freeing those is what the ordinary path does, and doing it here would pull them out
    /// from under native code mid-call: a heap corruption that surfaces later, somewhere
    /// else, as an unexplained crash. One leaked capture context is the better trade. The
    /// thread is already cancelled, so it exits on its own the moment FFmpeg returns.
    /// </para>
    /// <para>
    /// This source cannot be used again — its buffers now belong to that thread — so
    /// callers must build a new one. The stall watchdog already does, since it replaces
    /// the capture rather than reviving it.
    /// </para>
    /// </summary>
    private void AbandonUnsafe()
    {
        _abandoned = true;

        _logger.LogError(
            "The capture thread is still inside FFmpeg after 3s. Its buffers are being left "
            + "allocated rather than freed underneath it; this capture is abandoned and a new "
            + "one must be opened.");
    }

    private void ReleaseUnsafe()
    {
        _cts?.Dispose();
        _cts = null;

        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_decoder != null) { var d = _decoder; ffmpeg.avcodec_free_context(&d); _decoder = null; }

        if (_format != null)
        {
            var format = _format;
            ffmpeg.avformat_close_input(&format);
            _format = null;
        }

        if (_scaler != null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        _lastSourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _streamIndex = -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
