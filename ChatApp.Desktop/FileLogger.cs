using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace ChatApp.Desktop;

/// <summary>
/// Appends log output to a rolling file.
/// <para>
/// The desktop head is a <c>WinExe</c>, so it has no console and the console logger is
/// invisible. A call that fails to connect has to leave a trace somewhere, and this is
/// it: <c>%APPDATA%/ChatFlow/logs/chatflow.log</c>.
/// </para>
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>Rolled at 8 MB so a long call cannot fill the disk.</summary>
    private const long MaxBytes = 8 * 1024 * 1024;

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 4096);
    private readonly Thread _writer;
    private readonly string _path;
    private bool _disposed;

    public static string LogPath => Path.Combine(LogDirectory, "chatflow.log");

    public static string LogDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create);

            var dir = Path.Combine(root, "ChatFlow", "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public FileLoggerProvider()
    {
        _path = LogPath;
        Roll();

        // A dedicated writer thread keeps file I/O off the media and UI threads; a
        // blocking write on the capture callback would drop audio.
        _writer = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "chatflow-log-writer"
        };
        _writer.Start();
    }

    private void Roll()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes)
                return;

            var previous = _path + ".1";
            if (File.Exists(previous))
                File.Delete(previous);

            File.Move(_path, previous);
        }
        catch
        {
            // A locked or unwritable log file must never stop the app from starting.
        }
    }

    private void WriteLoop()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            foreach (var line in _queue.GetConsumingEnumerable())
                writer.WriteLine(line);
        }
        catch
        {
            // Losing logs is preferable to crashing the app.
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _queue);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger(string category, BlockingCollection<string> queue) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var shortCategory = category.Contains('.')
                ? category[(category.LastIndexOf('.') + 1)..]
                : category;

            var line = $"{DateTime.Now:HH:mm:ss.fff} {logLevel,-11} {shortCategory,-22} {formatter(state, exception)}";

            if (exception is not null)
                line += Environment.NewLine + exception;

            // Never block the caller: dropping a line under pressure beats stalling the
            // audio thread.
            queue.TryAdd(line);
        }
    }
}
