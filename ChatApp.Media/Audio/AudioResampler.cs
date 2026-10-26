using ChatApp.Media.Codec;
using FFmpeg.AutoGen;

namespace ChatApp.Media.Audio;

/// <summary>
/// Converts captured audio to the 48 kHz stereo format the Opus encoder expects.
/// <para>
/// Uses FFmpeg's swresample rather than hand-rolled interpolation: sample-rate
/// conversion needs a proper anti-aliasing filter, and naive linear interpolation from
/// 44.1 kHz produces audible aliasing on speech sibilants. It also handles channel
/// up/down-mixing in the same pass.
/// </para>
/// <para>
/// Not thread-safe; it lives on the capture thread.
/// </para>
/// </summary>
public sealed unsafe class AudioResampler : IDisposable
{
    private readonly int _targetSampleRate;
    private readonly int _targetChannels;

    private SwrContext* _context;
    private int _sourceSampleRate;
    private int _sourceChannels;
    private int _outputChannels;

    /// <summary>Holds the mono-to-stereo duplication result.</summary>
    private short[] _output = [];

    /// <summary>Separate scratch for swresample so the two paths cannot overwrite each other.</summary>
    private short[] _swrOutput = [];
    private bool _disposed;

    /// <summary>True when input already matches the target and conversion is a straight copy.</summary>
    public bool IsPassthrough { get; private set; }

    public AudioResampler(int targetSampleRate = 48_000, int targetChannels = 2)
    {
        FFmpegLoader.EnsureAvailable();

        _targetSampleRate = targetSampleRate;
        _targetChannels = targetChannels;
    }

    /// <summary>
    /// Converts interleaved 16-bit input, returning interleaved 16-bit output at the
    /// target format. The returned span is owned by this instance and is overwritten by
    /// the next call.
    /// </summary>
    public ReadOnlySpan<short> Convert(ReadOnlySpan<short> interleaved, int sourceSampleRate, int sourceChannels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (interleaved.IsEmpty)
            return ReadOnlySpan<short>.Empty;

        if (sourceSampleRate == _targetSampleRate && sourceChannels == _targetChannels)
        {
            IsPassthrough = true;
            return interleaved;
        }

        IsPassthrough = false;

        // Mono to stereo is handled here rather than by swresample's rematrix, which
        // applies an equal-power 1/sqrt(2) attenuation. WebRTC always negotiates mono
        // Opus, so leaving that in place would make every browser participant a
        // consistent 3 dB quieter than native stereo ones. Voice should play at the level
        // the speaker intended regardless of their channel count.
        if (sourceChannels == 1 && _targetChannels == 2 && sourceSampleRate == _targetSampleRate)
            return DuplicateMonoToStereo(interleaved);

        // Mono at a different rate: convert mono to mono so no rematrix happens, then
        // duplicate at unity gain.
        if (sourceChannels == 1 && _targetChannels == 2)
        {
            var resampledMono = ConvertWithSwr(interleaved, sourceSampleRate, sourceChannels: 1, outputChannels: 1);
            return DuplicateMonoToStereo(resampledMono);
        }

        return ConvertWithSwr(interleaved, sourceSampleRate, sourceChannels, _targetChannels);
    }

    private ReadOnlySpan<short> DuplicateMonoToStereo(ReadOnlySpan<short> mono)
    {
        var required = mono.Length * 2;
        if (_output.Length < required)
            _output = new short[required];

        for (var i = 0; i < mono.Length; i++)
        {
            _output[i * 2] = mono[i];
            _output[i * 2 + 1] = mono[i];
        }

        return _output.AsSpan(0, required);
    }

    private ReadOnlySpan<short> ConvertWithSwr(
        ReadOnlySpan<short> interleaved,
        int sourceSampleRate,
        int sourceChannels,
        int outputChannels)
    {
        EnsureContext(sourceSampleRate, sourceChannels, outputChannels);

        var inputSamplesPerChannel = interleaved.Length / sourceChannels;

        // Round up and add slack: swr may hold samples between calls, so a converted
        // block can be slightly longer than the linear ratio suggests.
        var maxOutputPerChannel = (int)ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(_context, sourceSampleRate) + inputSamplesPerChannel,
            _targetSampleRate,
            sourceSampleRate,
            AVRounding.AV_ROUND_UP);

        var required = maxOutputPerChannel * outputChannels;
        if (_swrOutput.Length < required)
            _swrOutput = new short[required];

        fixed (short* input = interleaved)
        fixed (short* output = _swrOutput)
        {
            var inputPtr = (byte*)input;
            var outputPtr = (byte*)output;

            var converted = ffmpeg.swr_convert(
                _context,
                &outputPtr, maxOutputPerChannel,
                &inputPtr, inputSamplesPerChannel);

            if (converted < 0)
            {
                FFmpegLoader.ThrowIfError(converted, "swr_convert");
                return ReadOnlySpan<short>.Empty;
            }

            return _swrOutput.AsSpan(0, converted * outputChannels);
        }
    }

    private void EnsureContext(int sourceSampleRate, int sourceChannels, int outputChannels)
    {
        if (_context != null
            && _sourceSampleRate == sourceSampleRate
            && _sourceChannels == sourceChannels
            && _outputChannels == outputChannels)
        {
            return;
        }

        Release();

        AVChannelLayout inLayout;
        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&inLayout, sourceChannels);
        ffmpeg.av_channel_layout_default(&outLayout, outputChannels);

        SwrContext* context = null;
        var result = ffmpeg.swr_alloc_set_opts2(
            &context,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, _targetSampleRate,
            &inLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, sourceSampleRate,
            0, null);

        FFmpegLoader.ThrowIfError(result, "swr_alloc_set_opts2");

        if (context == null)
            throw new InvalidOperationException("Could not allocate a resampler context.");

        _context = context;
        FFmpegLoader.ThrowIfError(ffmpeg.swr_init(_context), "swr_init");

        _sourceSampleRate = sourceSampleRate;
        _sourceChannels = sourceChannels;
        _outputChannels = outputChannels;
    }

    private void Release()
    {
        if (_context == null)
            return;

        var context = _context;
        ffmpeg.swr_free(&context);
        _context = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}
