using System.Runtime.ExceptionServices;
using System.Windows;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;
using Blaze.Provider.Radar;
using Microsoft.Extensions.Logging.Abstractions;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Configuration;

namespace Blaze.Provider.Radar.Ui.Tests;

public sealed class RadarSettingsViewFactoryTests
{
    [Fact]
    public async Task FactoryCreatesTheExistingMultiScreenRadarControlWindowUnderANeutralApplication()
    {
        using var providerDirectory = new TemporaryDirectory();
        var configuration = new RadarAppConfiguration { Screens = [] };
        var services = new DictionaryServiceProvider(new Dictionary<Type, object>
        {
            [typeof(RadarAppConfiguration)] = configuration,
            [typeof(IRadarSensorPipelineFactory)] = new RadarSensorPipelineFactory(NullLoggerFactory.Instance)
        });
        var plugin = new RadarPlugin();
        var provider = plugin.CreateProvider(new ProviderCreateContext(providerDirectory.Path, services));
        await provider.InitializeAsync(
            new ProviderInitializationContext(
                [new InteractionSurface
                {
                    SurfaceId = "front",
                    Name = "Front",
                    LogicalWidth = 1920,
                    LogicalHeight = 1080,
                    IsPrimary = true,
                    Order = 0
                }],
                EmptyServiceProvider.Instance),
            CancellationToken.None);
        var settingsFactory = Assert.IsAssignableFrom<IProviderSettingsViewFactory>(plugin.SettingsViewFactory);
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var view = settingsFactory.CreateView(provider, EmptySettingsContext.Instance);
                var window = Assert.IsType<Yuexin.Radar.Bridge.Wpf.MainWindow>(view);
                Assert.Equal("RadarBridge · 多屏雷达控制台", window.Title);
                Assert.IsType<MainViewModel>(window.DataContext);
                window.Close();
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Radar settings window creation timed out.");
        failure?.Throw();
        await provider.DisposeAsync();
    }

    private sealed class DictionaryServiceProvider(IReadOnlyDictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.TryGetValue(serviceType, out var service) ? service : null;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }

    private sealed class EmptySettingsContext : IProviderSettingsContext
    {
        public static EmptySettingsContext Instance { get; } = new();
        public ValueTask<string?> GetValueAsync(string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);
        public ValueTask SetValueAsync(string key, string? value, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Blaze.Provider.Radar.Ui.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
