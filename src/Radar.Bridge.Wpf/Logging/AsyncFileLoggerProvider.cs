using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Yuexin.Radar.Bridge.Wpf.Logging;

public sealed class AsyncFileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private readonly Channel<string> _entries;
    private readonly Task _writerTask;
    private int _disposed;

    public AsyncFileLoggerProvider(string path, int capacity = 1_024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _entries = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = WriteEntriesAsync(Path.GetFullPath(path));
    }

    public ILogger CreateLogger(string categoryName) => new AsyncFileLogger(categoryName, _entries.Writer);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _entries.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
    }

    public static string GetDefaultLogPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadarControl",
            "logs");
        return Path.Combine(directory, $"RadarBridge-{DateTimeOffset.Now:yyyyMMdd}.log");
    }

    private async Task WriteEntriesAsync(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 16_384,
            useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await foreach (var entry in _entries.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await writer.WriteLineAsync(entry).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    private sealed class AsyncFileLogger(string categoryName, ChannelWriter<string> writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:O} [{logLevel}] {categoryName} {message}");
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            writer.TryWrite(line);
        }
    }
}
