namespace ChatApp.Media.Audio;

/// <summary>
/// Repacks a stream of variable-sized capture buffers into the fixed-size frames Opus
/// requires.
/// <para>
/// Capture backends deliver whatever period the device gives them — 10 ms, 20 ms, or an
/// irregular size after a glitch — while the encoder needs exactly 960 samples per
/// channel every time. This accumulates across callbacks and emits only whole frames,
/// carrying the remainder forward.
/// </para>
/// <para>
/// Not thread-safe: capture callbacks arrive on a single device thread, and adding a
/// lock on the hot audio path would be wasted work.
/// </para>
/// </summary>
public sealed class AudioFrameAssembler
{
    private readonly int _frameSamplesPerChannel;
    private readonly int _channels;
    private readonly short[] _buffer;
    private int _filled;

    /// <summary>Total interleaved samples in one output frame.</summary>
    public int FrameLength => _frameSamplesPerChannel * _channels;

    /// <summary>Interleaved samples buffered but not yet emitted.</summary>
    public int Pending => _filled;

    public AudioFrameAssembler(int frameSamplesPerChannel = 960, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameSamplesPerChannel, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _frameSamplesPerChannel = frameSamplesPerChannel;
        _channels = channels;
        _buffer = new short[frameSamplesPerChannel * channels];
    }

    /// <summary>
    /// Adds interleaved samples and invokes <paramref name="onFrame"/> once per complete
    /// frame. The span passed to the callback is only valid for that call.
    /// </summary>
    public void Append(ReadOnlySpan<short> interleaved, AudioFrameHandler onFrame)
    {
        while (!interleaved.IsEmpty)
        {
            var needed = _buffer.Length - _filled;
            var take = Math.Min(needed, interleaved.Length);

            interleaved[..take].CopyTo(_buffer.AsSpan(_filled));
            _filled += take;
            interleaved = interleaved[take..];

            if (_filled < _buffer.Length)
                return;

            _filled = 0;
            onFrame(_buffer);
        }
    }

    /// <summary>
    /// Discards buffered samples. Called when capture restarts, so a partial frame from
    /// the previous device does not get spliced onto the new stream.
    /// </summary>
    public void Reset() => _filled = 0;
}

/// <summary>Receives one complete audio frame. The span is valid only during the call.</summary>
public delegate void AudioFrameHandler(ReadOnlySpan<short> frame);
