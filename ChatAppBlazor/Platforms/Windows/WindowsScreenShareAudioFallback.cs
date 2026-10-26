#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wasapi.CoreAudioApi;
using NAudio.Wave;

namespace ChatAppBlazor.Platforms.Windows;

internal sealed class WindowsScreenShareAudioFallback : IDisposable
{
    private const int HResultFileNotFound = unchecked((int)0x80070002);

    /// <summary>Minimum audio accumulated before a chunk is handed to the WebView.</summary>
    private const int ChunkFlushMilliseconds = 50;

    private readonly object _sync = new();
    private readonly object _chunkSync = new();
    private IWaveIn? _capture;
    private bool _disposed;
    private bool _reportedUnsupportedFormat;

    private byte[] _pendingPcm = Array.Empty<byte>();
    private int _pendingPcmLength;
    private int _pendingSampleRate;
    private int _pendingChannels;
    /// <summary>Capture that owns the pending batch; guarded by <see cref="_chunkSync"/>.</summary>
    private IWaveIn? _batchOwner;

    private string _activeMode = "system";
    private uint? _activeProcessId;
    private string? _activeWindowTitle;

    public event Action<AudioChunk>? ChunkCaptured;
    public event Action<string>? CaptureError;

    public StartResult Start(StartRequest request = default)
    {
        var capture = CreateCapture(request, out var mode, out var processId, out var windowTitle);

        lock (_sync)
        {
            ThrowIfDisposed();
            ActivateCaptureUnsafe(capture, mode, processId, windowTitle);
        }

        try
        {
            capture.StartRecording();
        }
        catch (Exception startException)
        {
            lock (_sync)
            {
                StopInternalUnsafe();
            }

            var unwrappedStartException = UnwrapAggregateException(startException);
            if (mode == "process" && processId.HasValue)
            {
                var preferExactProcess = ShouldPreferExactProcessCapture(
                    processId.Value,
                    windowTitle,
                    request.TargetWindowLabel);

                CaptureError?.Invoke(
                    $"Process loopback include-tree activation failed ({DescribeException(unwrappedStartException)}), retrying exact-process mode.");

                var errors = new List<string>
                {
                    $"pid {processId.Value} include-tree: {DescribeException(unwrappedStartException)}"
                };

                if (TryStartProcessCapture(processId.Value, includeProcessTree: false, windowTitle, out var samePidExactError))
                {
                    return new StartResult(_activeMode, _activeProcessId, _activeWindowTitle);
                }
                errors.Add($"pid {processId.Value} exact-process: {DescribeException(samePidExactError)}");

                if (!preferExactProcess &&
                    ShouldTryParentProcessFallback(unwrappedStartException, samePidExactError) &&
                    TryGetParentProcessId(processId.Value, out var parentPid) &&
                    parentPid != 0 &&
                    parentPid != processId.Value)
                {
                    CaptureError?.Invoke(
                        $"Retrying process loopback with parent process ID {parentPid} for window \"{windowTitle ?? request.TargetWindowLabel ?? "(unknown)"}\".");

                    if (TryStartProcessCapture(parentPid, includeProcessTree: true, windowTitle, out var parentIncludeError))
                    {
                        return new StartResult(_activeMode, _activeProcessId, _activeWindowTitle);
                    }
                    errors.Add($"pid {parentPid} include-tree: {DescribeException(parentIncludeError)}");

                    if (TryStartProcessCapture(parentPid, includeProcessTree: false, windowTitle, out var parentExactError))
                    {
                        return new StartResult(_activeMode, _activeProcessId, _activeWindowTitle);
                    }
                    errors.Add($"pid {parentPid} exact-process: {DescribeException(parentExactError)}");
                }

                throw new InvalidOperationException(
                    $"Process loopback capture failed after retries: {string.Join("; ", errors)}",
                    samePidExactError);
            }

            throw new InvalidOperationException(DescribeException(unwrappedStartException), unwrappedStartException);
        }

        return new StartResult(_activeMode, _activeProcessId, _activeWindowTitle);
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopInternalUnsafe();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private IWaveIn CreateCapture(
        StartRequest request,
        out string mode,
        out uint? processId,
        out string? windowTitle)
    {
        mode = "system";
        processId = null;
        windowTitle = null;

        if (request.PreferProcessIsolation)
        {
            if (!IsProcessLoopbackSupported())
            {
                var unsupportedMessage =
                    "Process-isolated app audio capture requires Windows build 20348+ (Windows 11 / Server 2022 or newer).";
                if (!request.AllowSystemFallback)
                {
                    throw new NotSupportedException(unsupportedMessage);
                }

                CaptureError?.Invoke($"{unsupportedMessage} Falling back to system loopback.");
                return new WasapiLoopbackCapture();
            }

            if (!TryResolveWindowProcessId(request.TargetWindowLabel, out var resolvedPid, out var resolvedTitle))
            {
                var resolveMessage = $"Could not resolve process for shared window \"{request.TargetWindowLabel ?? "(unknown)"}\".";
                if (!request.AllowSystemFallback)
                {
                    throw new InvalidOperationException(resolveMessage);
                }

                CaptureError?.Invoke($"{resolveMessage} Falling back to system loopback.");
                return new WasapiLoopbackCapture();
            }

            if (!TryValidateProcessId(resolvedPid, out var processValidationError))
            {
                if (!request.AllowSystemFallback)
                {
                    throw new InvalidOperationException(processValidationError);
                }

                CaptureError?.Invoke($"{processValidationError} Falling back to system loopback.");
                return new WasapiLoopbackCapture();
            }

            try
            {
                var includeProcessTree = !ShouldPreferExactProcessCapture(
                    resolvedPid,
                    resolvedTitle,
                    request.TargetWindowLabel);

                var processCapture = new ProcessLoopbackCapture(resolvedPid, includeProcessTree);
                mode = "process";
                processId = resolvedPid;
                windowTitle = resolvedTitle;
                return processCapture;
            }
            catch (Exception ex)
            {
                if (!request.AllowSystemFallback)
                {
                    throw new InvalidOperationException(
                        $"Process loopback capture failed for window \"{resolvedTitle ?? request.TargetWindowLabel ?? "(unknown)"}\".",
                        ex);
                }

                CaptureError?.Invoke($"Process loopback capture failed ({ex.Message}), falling back to system loopback.");
            }
        }

        return new WasapiLoopbackCapture();
    }

    private static bool TryValidateProcessId(uint processId, out string error)
    {
        error = string.Empty;

        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            _ = process.ProcessName;
            return true;
        }
        catch (ArgumentException)
        {
            error = $"Resolved process ID {processId} no longer exists.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Resolved process ID {processId} is not accessible ({ex.Message}).";
            return false;
        }
    }

    private static bool ShouldPreferExactProcessCapture(
        uint processId,
        string? resolvedWindowTitle = null,
        string? requestedWindowLabel = null)
    {
        if (ContainsVlcToken(resolvedWindowTitle) || ContainsVlcToken(requestedWindowLabel))
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            var processName = process.ProcessName ?? string.Empty;
            // VLC frequently duplicates audio when include-tree loopback is used.
            if (ContainsVlcToken(processName))
            {
                return true;
            }
        }
        catch
        {
            // Best-effort heuristic only.
        }

        return false;
    }

    private static bool ContainsVlcToken(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains("vlc", StringComparison.OrdinalIgnoreCase);

    private bool TryStartProcessCapture(
        uint processId,
        bool includeProcessTree,
        string? windowTitle,
        out Exception error)
    {
        var capture = new ProcessLoopbackCapture(processId, includeProcessTree);
        lock (_sync)
        {
            ThrowIfDisposed();
            ActivateCaptureUnsafe(capture, "process", processId, windowTitle);
        }

        try
        {
            capture.StartRecording();
            error = new InvalidOperationException("No error.");
            return true;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                StopInternalUnsafe();
            }

            error = UnwrapAggregateException(ex);
            return false;
        }
    }

    private static bool ShouldTryParentProcessFallback(params Exception[] errors)
    {
        foreach (var error in errors)
        {
            var unwrapped = UnwrapAggregateException(error);
            if (unwrapped is COMException comException && comException.HResult == HResultFileNotFound)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetParentProcessId(uint processId, out uint parentProcessId)
    {
        parentProcessId = 0;

        var snapshot = CreateToolhelp32Snapshot(SnapshotFlagsProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return false;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<ProcessEntry32>()
            };

            if (!Process32First(snapshot, ref entry))
            {
                return false;
            }

            do
            {
                if (entry.th32ProcessID == processId)
                {
                    parentProcessId = entry.th32ParentProcessID;
                    return parentProcessId != 0;
                }
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        return false;
    }

    private static bool IsProcessLoopbackSupported()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        if (sender is not IWaveIn source)
        {
            return;
        }

        var format = source.WaveFormat;

        bool converted;
        AudioChunk? batched = null;
        lock (_chunkSync)
        {
            // Unsubscribing DataAvailable does not cancel an invocation already dispatched on the
            // WASAPI thread, so a buffer from a torn-down capture can arrive after teardown has
            // cleared the batch. Gate on ownership under the same lock that guards the batch,
            // otherwise that stale audio is prepended to the next capture session.
            if (!ReferenceEquals(source, _batchOwner))
            {
                return;
            }

            converted = TryAppendFloat32InterleavedUnsafe(format, e.Buffer, e.BytesRecorded);
            if (converted)
            {
                batched = TryTakeBatchedChunkUnsafe();
            }
        }

        if (!converted)
        {
            if (_reportedUnsupportedFormat)
            {
                return;
            }

            _reportedUnsupportedFormat = true;
            CaptureError?.Invoke(
                $"Unsupported loopback format: {format.Encoding} {format.BitsPerSample}bit, {format.Channels}ch @ {format.SampleRate}Hz.");
            Stop();
            return;
        }

        if (batched.HasValue)
        {
            ChunkCaptured?.Invoke(batched.Value);
        }
    }

    /// <summary>
    /// Appends one WASAPI capture buffer to the pending batch as interleaved 32-bit float.
    /// Returns false when the source format is not one we can convert.
    /// </summary>
    private bool TryAppendFloat32InterleavedUnsafe(WaveFormat format, byte[] buffer, int bytesRecorded)
    {
        var isFloat32 = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        var isPcm16 = format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16;

        int requiredBytes;
        if (isFloat32)
        {
            requiredBytes = bytesRecorded;
        }
        else if (isPcm16)
        {
            requiredBytes = bytesRecorded / 2 * sizeof(float);
        }
        else
        {
            return false;
        }

        // A format change mid-capture invalidates whatever is already batched.
        if (_pendingPcmLength > 0 &&
            (_pendingSampleRate != format.SampleRate || _pendingChannels != format.Channels))
        {
            _pendingPcmLength = 0;
        }

        _pendingSampleRate = format.SampleRate;
        _pendingChannels = Math.Max(1, format.Channels);

        EnsurePendingCapacityUnsafe(_pendingPcmLength + requiredBytes);

        if (isFloat32)
        {
            Buffer.BlockCopy(buffer, 0, _pendingPcm, _pendingPcmLength, bytesRecorded);
            _pendingPcmLength += bytesRecorded;
            return true;
        }

        var destination = _pendingPcm.AsSpan(_pendingPcmLength, requiredBytes);
        var readOffset = 0;
        var writeOffset = 0;

        while (readOffset + 1 < bytesRecorded)
        {
            var sample = (short)(buffer[readOffset] | (buffer[readOffset + 1] << 8));
            var normalized = Math.Clamp(sample / 32768f, -1f, 1f);
            BitConverter.TryWriteBytes(destination[writeOffset..], normalized);
            readOffset += 2;
            writeOffset += sizeof(float);
        }

        _pendingPcmLength += writeOffset;
        return true;
    }

    private void EnsurePendingCapacityUnsafe(int requiredBytes)
    {
        if (_pendingPcm.Length >= requiredBytes)
        {
            return;
        }

        var capacity = Math.Max(requiredBytes, Math.Max(8192, _pendingPcm.Length * 2));
        Array.Resize(ref _pendingPcm, capacity);
    }

    /// <summary>
    /// Emits a chunk only once at least <see cref="ChunkFlushMilliseconds"/> of audio has
    /// accumulated. WASAPI hands us a buffer roughly every 10 ms, and each emitted chunk costs a
    /// base64 encode, a JSON serialize, a UI-thread dispatch and a cross-process WebView post —
    /// batching cuts all four by ~5x for a proportional increase in playout latency.
    /// </summary>
    private AudioChunk? TryTakeBatchedChunkUnsafe()
    {
        if (_pendingPcmLength <= 0 || _pendingSampleRate <= 0 || _pendingChannels <= 0)
        {
            return null;
        }

        var bytesPerFrame = _pendingChannels * sizeof(float);
        var thresholdBytes = (int)((long)_pendingSampleRate * ChunkFlushMilliseconds / 1000) * bytesPerFrame;
        if (thresholdBytes <= 0 || _pendingPcmLength < thresholdBytes)
        {
            return null;
        }

        var base64Data = Convert.ToBase64String(_pendingPcm.AsSpan(0, _pendingPcmLength));
        var chunk = new AudioChunk(_pendingSampleRate, _pendingChannels, base64Data);
        _pendingPcmLength = 0;
        return chunk;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            _capture = null;
            _activeMode = "system";
            _activeProcessId = null;
            _activeWindowTitle = null;
        }

        lock (_chunkSync)
        {
            _batchOwner = null;
            _pendingPcmLength = 0;
        }

        if (sender is IWaveIn source)
        {
            source.DataAvailable -= OnDataAvailable;
            source.RecordingStopped -= OnRecordingStopped;
            source.Dispose();
        }

        if (e.Exception != null)
        {
            CaptureError?.Invoke($"Loopback capture stopped: {e.Exception.Message}");
        }
    }

    private void StopInternalUnsafe()
    {
        var capture = _capture;
        _capture = null;
        _activeMode = "system";
        _activeProcessId = null;
        _activeWindowTitle = null;

        // Disown the batch and drop any sub-threshold tail. Clearing the owner is what actually
        // stops a late DataAvailable callback from resurrecting the batch.
        // Nesting is only ever _sync -> _chunkSync, never the reverse.
        lock (_chunkSync)
        {
            _batchOwner = null;
            _pendingPcmLength = 0;
        }

        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;

        try
        {
            capture.StopRecording();
        }
        catch
        {
            // Best-effort shutdown.
        }
        finally
        {
            capture.Dispose();
        }
    }

    private void ActivateCaptureUnsafe(IWaveIn capture, string mode, uint? processId, string? windowTitle)
    {
        StopInternalUnsafe();

        _capture = capture;

        // Claim the batch for this capture before wiring up events, so the first callback is
        // never rejected as stale.
        lock (_chunkSync)
        {
            _batchOwner = capture;
            _pendingPcmLength = 0;
        }

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        _reportedUnsupportedFormat = false;
        _activeMode = mode;
        _activeProcessId = processId;
        _activeWindowTitle = windowTitle;
    }

    private static Exception UnwrapAggregateException(Exception exception)
    {
        while (exception is AggregateException aggregateException &&
               aggregateException.InnerExceptions.Count == 1 &&
               aggregateException.InnerException != null)
        {
            exception = aggregateException.InnerException;
        }

        return exception;
    }

    private static string DescribeException(Exception exception)
    {
        var unwrapped = UnwrapAggregateException(exception);
        if (unwrapped is COMException comException)
        {
            return $"{comException.Message} (HRESULT=0x{comException.HResult:X8})";
        }

        return unwrapped.Message;
    }

    private static bool TryResolveWindowProcessId(string? targetLabel, out uint processId, out string? matchedWindowTitle)
    {
        processId = 0;
        matchedWindowTitle = null;

        if (TryResolveProcessIdFromWindowToken(targetLabel, out processId, out matchedWindowTitle))
        {
            return true;
        }

        var normalizedTarget = NormalizeWindowText(targetLabel);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return false;
        }

        WindowCandidate? best = null;
        var bestScore = int.MinValue;

        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            var titleLength = GetWindowTextLength(hwnd);
            if (titleLength <= 0)
            {
                return true;
            }

            var titleBuilder = new StringBuilder(titleLength + 1);
            _ = GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return true;
            }

            var score = ScoreWindowTitle(normalizedTarget, NormalizeWindowText(title));
            if (score > bestScore)
            {
                bestScore = score;
                best = new WindowCandidate(pid, title);
            }

            _ = lParam;
            return true;
        }, IntPtr.Zero);

        if (best is null || bestScore < 40)
        {
            return false;
        }

        processId = best.Value.ProcessId;
        matchedWindowTitle = best.Value.Title;
        return true;
    }

    private static bool TryResolveProcessIdFromWindowToken(
        string? targetLabel,
        out uint processId,
        out string? windowTitle)
    {
        processId = 0;
        windowTitle = null;

        if (string.IsNullOrWhiteSpace(targetLabel))
        {
            return false;
        }

        // WebView2 can expose labels like "window:81201836:0" for window capture.
        // The middle token may be an HWND identifier (or lower 32 bits of one).
        var trimmed = targetLabel.Trim();
        if (!trimmed.StartsWith("window:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tokens = trimmed.Split(':', StringSplitOptions.TrimEntries);
        if (tokens.Length < 2 || !ulong.TryParse(tokens[1], out var rawWindowId))
        {
            return false;
        }

        if (TryResolveByWindowHandle(rawWindowId, out processId, out windowTitle))
        {
            return true;
        }

        var targetLow32 = (uint)(rawWindowId & 0xFFFF_FFFF);
        WindowCandidate? match = null;
        EnumWindows((hwnd, lParam) =>
        {
            var hwndValue = unchecked((ulong)(long)hwnd);
            var hwndLow32 = (uint)(hwndValue & 0xFFFF_FFFF);
            if (hwndLow32 != targetLow32)
            {
                return true;
            }

            if (TryGetProcessFromWindowHandle(hwnd, out var pid, out var title))
            {
                match = new WindowCandidate(pid, title ?? string.Empty);
                return false;
            }

            return true;
        }, IntPtr.Zero);

        if (match is null)
        {
            return false;
        }

        processId = match.Value.ProcessId;
        windowTitle = string.IsNullOrWhiteSpace(match.Value.Title) ? null : match.Value.Title;
        return true;
    }

    private static bool TryResolveByWindowHandle(
        ulong rawWindowId,
        out uint processId,
        out string? windowTitle)
    {
        processId = 0;
        windowTitle = null;

        // Candidate 1: direct pointer-sized cast.
        var directHandle = new IntPtr(unchecked((long)rawWindowId));
        if (TryGetProcessFromWindowHandle(directHandle, out processId, out windowTitle))
        {
            return true;
        }

        // Candidate 2: lower-32-bit signed cast (common when only low bits are available).
        var low32SignedHandle = new IntPtr(unchecked((int)(rawWindowId & 0xFFFF_FFFF)));
        if (TryGetProcessFromWindowHandle(low32SignedHandle, out processId, out windowTitle))
        {
            return true;
        }

        // Candidate 3: lower-32-bit unsigned cast.
        var low32UnsignedHandle = new IntPtr(unchecked((int)(uint)(rawWindowId & 0xFFFF_FFFF)));
        return TryGetProcessFromWindowHandle(low32UnsignedHandle, out processId, out windowTitle);
    }

    private static bool TryGetProcessFromWindowHandle(
        IntPtr hwnd,
        out uint processId,
        out string? windowTitle)
    {
        processId = 0;
        windowTitle = null;

        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return false;
        }

        processId = pid;
        windowTitle = GetWindowTitle(hwnd);
        return true;
    }

    private static string? GetWindowTitle(IntPtr hwnd)
    {
        var titleLength = GetWindowTextLength(hwnd);
        if (titleLength <= 0)
        {
            return null;
        }

        var titleBuilder = new StringBuilder(titleLength + 1);
        _ = GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
        var title = titleBuilder.ToString().Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static int ScoreWindowTitle(string normalizedTarget, string normalizedWindowTitle)
    {
        if (normalizedTarget.Equals(normalizedWindowTitle, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (normalizedWindowTitle.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
            normalizedTarget.Contains(normalizedWindowTitle, StringComparison.OrdinalIgnoreCase))
        {
            return 85;
        }

        var targetTokens = normalizedTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targetTokens.Length == 0)
        {
            return 0;
        }

        var hits = 0;
        foreach (var token in targetTokens)
        {
            if (normalizedWindowTitle.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                hits++;
            }
        }

        if (hits == 0)
        {
            return 0;
        }

        return 40 + (hits * 10);
    }

    private static string NormalizeWindowText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Trim();
        if (text.StartsWith("Window:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["Window:".Length..].Trim();
        }

        if (text.StartsWith("\"") && text.EndsWith("\"") && text.Length > 1)
        {
            text = text[1..^1];
        }

        return text.Trim();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsScreenShareAudioFallback));
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private readonly record struct WindowCandidate(uint ProcessId, string Title);

    private const uint SnapshotFlagsProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    internal readonly record struct StartRequest(
        bool PreferProcessIsolation,
        string? TargetWindowLabel,
        bool AllowSystemFallback);

    internal readonly record struct StartResult(
        string Mode,
        uint? ProcessId,
        string? WindowTitle);

    internal readonly record struct AudioChunk(
        int SampleRate,
        int Channels,
        string Base64Data);

    private sealed class ProcessLoopbackCapture : IWaveIn
    {
        private static readonly object NativeSync = new();
        private static readonly ApplicationLoopbackNative.AudioCallbackDelegate NativeAudioCallback = OnNativeAudio;
        private static ProcessLoopbackCapture? s_activeCapture;

        private readonly SynchronizationContext? _syncContext;
        private readonly bool _includeProcessTree;
        private readonly uint _processId;
        private readonly object _stateSync = new();
        private readonly ManualResetEventSlim _nativeStartReturned = new(false);
        private Thread? _captureThread;
        private Exception? _captureThreadException;
        private int _recordingStoppedRaised;
        private bool _disposed;
        private CaptureState _captureState = CaptureState.Stopped;
        private WaveFormat _waveFormat;

        public ProcessLoopbackCapture(
            uint processId,
            bool includeProcessTree,
            int sampleRate = 48_000,
            int channels = 2,
            int bitsPerSample = 16)
        {
            _syncContext = SynchronizationContext.Current;
            _processId = processId;
            _includeProcessTree = includeProcessTree;
            _waveFormat = new WaveFormat(sampleRate, bitsPerSample, channels);
        }

        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public CaptureState CaptureState => _captureState;

        public WaveFormat WaveFormat
        {
            get => _waveFormat;
            set => _waveFormat = value;
        }

        public void StartRecording()
        {
            ApplicationLoopbackNative.EnsureLoaded();

            lock (_stateSync)
            {
                ThrowIfDisposed();
                if (_captureState != CaptureState.Stopped)
                {
                    throw new InvalidOperationException("Previous recording still in progress.");
                }

                _captureState = CaptureState.Starting;
                _captureThreadException = null;
                Interlocked.Exchange(ref _recordingStoppedRaised, 0);
                _nativeStartReturned.Reset();
            }

            RegisterActiveCapture(this);

            _captureThread = new Thread(CaptureThreadMain)
            {
                IsBackground = true,
                Name = "ApplicationLoopbackCaptureThread"
            };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();

            var returnedQuickly = _nativeStartReturned.Wait(TimeSpan.FromMilliseconds(800));
            if (returnedQuickly && _captureThreadException is not null)
            {
                lock (_stateSync)
                {
                    _captureState = CaptureState.Stopped;
                }

                UnregisterActiveCapture(this);
                throw _captureThreadException;
            }

            lock (_stateSync)
            {
                if (_captureState == CaptureState.Starting)
                {
                    _captureState = CaptureState.Capturing;
                }
            }
        }

        public void StopRecording()
        {
            lock (_stateSync)
            {
                if (_captureState == CaptureState.Stopped)
                {
                    return;
                }

                _captureState = CaptureState.Stopping;
            }

            UnregisterActiveCapture(this);

            Exception? stopException = null;
            try
            {
                var stopHr = ApplicationLoopbackNative.StopCaptureAsync();
                if (stopHr < 0)
                {
                    stopException = new COMException(
                        $"StopCaptureAsync failed (HRESULT=0x{stopHr:X8}).",
                        stopHr);
                }
            }
            catch (Exception ex)
            {
                stopException = ex;
            }

            lock (_stateSync)
            {
                _captureState = CaptureState.Stopped;
            }

            RaiseRecordingStoppedOnce(stopException);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopRecording();
            _nativeStartReturned.Dispose();

            if (_captureThread is { IsAlive: true })
            {
                _captureThread.Join(TimeSpan.FromMilliseconds(250));
            }

            _captureThread = null;
        }

        private void CaptureThreadMain()
        {
            try
            {
                var startHr = ApplicationLoopbackNative.StartCaptureAsync(
                    _processId,
                    _includeProcessTree,
                    unchecked((ushort)_waveFormat.Channels),
                    unchecked((uint)_waveFormat.SampleRate),
                    unchecked((ushort)_waveFormat.BitsPerSample));
                if (startHr < 0)
                {
                    throw new COMException(
                        $"ActivateAudioInterfaceAsync failed. (HRESULT=0x{startHr:X8})",
                        startHr);
                }
            }
            catch (Exception ex)
            {
                _captureThreadException = ex;
            }
            finally
            {
                _nativeStartReturned.Set();

                var shouldRaiseStopped = false;
                Exception? stopException = null;
                lock (_stateSync)
                {
                    if (_captureThreadException is not null)
                    {
                        stopException = _captureThreadException;
                        if (_captureState != CaptureState.Stopped)
                        {
                            _captureState = CaptureState.Stopped;
                            shouldRaiseStopped = true;
                        }
                    }
                    else if (_captureState == CaptureState.Stopping)
                    {
                        _captureState = CaptureState.Stopped;
                        shouldRaiseStopped = true;
                    }
                }

                if (_captureThreadException is not null)
                {
                    UnregisterActiveCapture(this);
                }

                if (shouldRaiseStopped)
                {
                    RaiseRecordingStoppedOnce(stopException);
                }
            }
        }

        private void HandleNativeAudio(IntPtr data, int length)
        {
            if (length <= 0 || data == IntPtr.Zero)
            {
                return;
            }

            lock (_stateSync)
            {
                if (_captureState != CaptureState.Capturing)
                {
                    return;
                }
            }

            var managedBuffer = new byte[length];
            Marshal.Copy(data, managedBuffer, 0, length);
            RaiseDataAvailable(managedBuffer, length);
        }

        private void RaiseDataAvailable(byte[] buffer, int bytesRecorded)
        {
            var handler = DataAvailable;
            if (handler is null)
            {
                return;
            }

            if (_syncContext is null)
            {
                handler(this, new WaveInEventArgs(buffer, bytesRecorded));
                return;
            }

            _syncContext.Post(_ => handler(this, new WaveInEventArgs(buffer, bytesRecorded)), null);
        }

        private void RaiseRecordingStoppedOnce(Exception? exception)
        {
            if (Interlocked.Exchange(ref _recordingStoppedRaised, 1) != 0)
            {
                return;
            }

            var handler = RecordingStopped;
            if (handler is null)
            {
                return;
            }

            if (_syncContext is null)
            {
                handler(this, new StoppedEventArgs(exception));
                return;
            }

            _syncContext.Post(_ => handler(this, new StoppedEventArgs(exception)), null);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ProcessLoopbackCapture));
            }
        }

        private static void RegisterActiveCapture(ProcessLoopbackCapture capture)
        {
            lock (NativeSync)
            {
                if (s_activeCapture is not null && !ReferenceEquals(s_activeCapture, capture))
                {
                    throw new InvalidOperationException(
                        "ApplicationLoopback.dll supports only one active process loopback capture at a time.");
                }

                ApplicationLoopbackNative.SetAudioCallback(NativeAudioCallback);
                s_activeCapture = capture;
            }
        }

        private static void UnregisterActiveCapture(ProcessLoopbackCapture capture)
        {
            lock (NativeSync)
            {
                if (ReferenceEquals(s_activeCapture, capture))
                {
                    s_activeCapture = null;
                }
            }
        }

        private static void OnNativeAudio(IntPtr data, uint length)
        {
            ProcessLoopbackCapture? activeCapture;
            lock (NativeSync)
            {
                activeCapture = s_activeCapture;
            }

            if (activeCapture is null)
            {
                return;
            }

            try
            {
                activeCapture.HandleNativeAudio(data, checked((int)length));
            }
            catch
            {
                // Keep native callback resilient.
            }
        }
    }

    private static class ApplicationLoopbackNative
    {
        private const string DllFileName = "ApplicationLoopback.dll";
        private static readonly object Sync = new();
        private static IntPtr s_libraryHandle;
        private static string? s_loadedPath;
        private static SetAudioCallbackDelegate? s_setAudioCallback;
        private static StartCaptureAsyncDelegate? s_startCaptureAsync;
        private static StopCaptureAsyncDelegate? s_stopCaptureAsync;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void AudioCallbackDelegate(IntPtr data, uint length);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SetAudioCallbackDelegate(AudioCallbackDelegate callback);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StartCaptureAsyncDelegate(
            uint processId,
            [MarshalAs(UnmanagedType.I1)] bool includeProcessTree,
            ushort channels,
            uint sampleRate,
            ushort bitsPerSample);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StopCaptureAsyncDelegate(IntPtr instance);

        internal static string EnsureLoaded()
        {
            lock (Sync)
            {
                if (s_libraryHandle != IntPtr.Zero)
                {
                    return s_loadedPath ?? DllFileName;
                }

                Exception? lastLoadException = null;
                var searchedPaths = GetCandidatePaths();
                foreach (var candidatePath in searchedPaths)
                {
                    try
                    {
                        if (!File.Exists(candidatePath))
                        {
                            continue;
                        }

                        if (!NativeLibrary.TryLoad(candidatePath, out s_libraryHandle) ||
                            s_libraryHandle == IntPtr.Zero)
                        {
                            continue;
                        }

                        s_setAudioCallback = GetExport<SetAudioCallbackDelegate>(s_libraryHandle, "SetAudioCallback");
                        s_startCaptureAsync = GetExport<StartCaptureAsyncDelegate>(s_libraryHandle, "StartCaptureAsync");
                        s_stopCaptureAsync = GetExport<StopCaptureAsyncDelegate>(s_libraryHandle, "StopCaptureAsync");
                        s_loadedPath = candidatePath;
                        return candidatePath;
                    }
                    catch (Exception ex)
                    {
                        lastLoadException = ex;
                        if (s_libraryHandle != IntPtr.Zero)
                        {
                            NativeLibrary.Free(s_libraryHandle);
                            s_libraryHandle = IntPtr.Zero;
                        }
                    }
                }

                var searched = string.Join(", ", searchedPaths);
                if (lastLoadException is null)
                {
                    throw new FileNotFoundException(
                        $"{DllFileName} not found. Place it next to the app executable or set APPLICATION_LOOPBACK_DLL_PATH. Searched: {searched}");
                }

                throw new InvalidOperationException(
                    $"Failed to load {DllFileName}. Searched: {searched}",
                    lastLoadException);
            }
        }

        internal static void SetAudioCallback(AudioCallbackDelegate callback)
        {
            _ = EnsureLoaded();
            s_setAudioCallback!(callback);
        }

        internal static int StartCaptureAsync(
            uint processId,
            bool includeProcessTree,
            ushort channels,
            uint sampleRate,
            ushort bitsPerSample)
        {
            _ = EnsureLoaded();
            return s_startCaptureAsync!(
                processId,
                includeProcessTree,
                channels,
                sampleRate,
                bitsPerSample);
        }

        internal static int StopCaptureAsync()
        {
            _ = EnsureLoaded();
            return s_stopCaptureAsync!(IntPtr.Zero);
        }

        private static IReadOnlyList<string> GetCandidatePaths()
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath))
                {
                    candidates.Add(fullPath);
                }
            }

            AddCandidate(Environment.GetEnvironmentVariable("APPLICATION_LOOPBACK_DLL_PATH"));
            AddCandidate(Path.Combine(AppContext.BaseDirectory, DllFileName));
            AddCandidate(Path.Combine(AppContext.BaseDirectory, "Platforms", "Windows", DllFileName));
            AddCandidate(Path.Combine(Environment.CurrentDirectory, DllFileName));

            return candidates;
        }

        private static T GetExport<T>(IntPtr moduleHandle, string exportName) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(moduleHandle, exportName, out var address) ||
                address == IntPtr.Zero)
            {
                throw new MissingMethodException(
                    $"{DllFileName} does not export \"{exportName}\".");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }
}
#endif
