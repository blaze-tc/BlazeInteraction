using Microsoft.Extensions.Logging;
using Yuexin.Radar.Bridge.Wpf.Logging;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class AsyncFileLoggerProviderTests
{
    [Fact]
    public async Task LogEntry_IsFlushedWhileBridgeIsStillRunning()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadarControl.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "bridge.log");
        try
        {
            await using var provider = new AsyncFileLoggerProvider(path);
            var logger = provider.CreateLogger("test");

            logger.LogInformation("live pipeline diagnostics");

            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while ((!File.Exists(path) || new FileInfo(path).Length == 0) &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            Assert.Contains("live pipeline diagnostics", await reader.ReadToEndAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
