using System.Windows.Input;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Bridge.Wpf.Tests;

public sealed class BridgeWindowCoordinatorTests
{
    [Fact]
    public void FirstRunSelector_OverridesMinimizedLaunch()
    {
        var fixture = Fixture.Create(launchMinimized: true, activeProviderId: null);

        fixture.Coordinator.Start(new BridgeSettings(1, null));

        var selector = Assert.Single(fixture.Windows.SelectorWindows);
        Assert.True(selector.Window.IsVisible);
        Assert.False(selector.Window.IsMinimized);
        Assert.False(selector.ViewModel.CanCancel);
    }

    [Fact]
    public void SavedProviderWindow_RespectsMinimizedLaunchAndWrapsContentOnce()
    {
        var fixture = Fixture.Create(
            launchMinimized: true,
            activeProviderId: "blaze.radar.f10f20");

        fixture.Coordinator.Start(new BridgeSettings(1, "blaze.radar.f10f20"));
        fixture.Host.RepublishActiveProvider();

        Assert.True(fixture.RadarWindow.IsVisible);
        Assert.True(fixture.RadarWindow.IsMinimized);
        Assert.Equal(1, fixture.Windows.WrapCalls);
        Assert.IsType<FakeWrappedContent>(fixture.RadarWindow.Content);
    }

    [Fact]
    public void ReturnToSelection_HidesSettingsWithoutStoppingProvider()
    {
        var fixture = Fixture.Create(
            launchMinimized: false,
            activeProviderId: "blaze.radar.f10f20");
        fixture.Coordinator.Start(new BridgeSettings(1, "blaze.radar.f10f20"));

        Assert.Single(fixture.Windows.Headers).ReturnToSelectionCommand.Execute(null);

        Assert.False(fixture.RadarWindow.IsVisible);
        Assert.False(fixture.RadarWindow.IsClosed);
        Assert.Equal(0, fixture.Host.SelectCalls);
        Assert.True(Assert.Single(fixture.Windows.SelectorWindows).ViewModel.CanCancel);
    }

    [Fact]
    public void CancellingReturnFlow_RestoresSameSettingsWindowInstance()
    {
        var fixture = Fixture.Create(
            launchMinimized: false,
            activeProviderId: "blaze.radar.f10f20");
        fixture.Coordinator.Start(new BridgeSettings(1, "blaze.radar.f10f20"));
        var original = fixture.RadarWindow;
        Assert.Single(fixture.Windows.Headers).ReturnToSelectionCommand.Execute(null);

        Assert.Single(fixture.Windows.SelectorWindows).ViewModel.CancelCommand.Execute(null);

        Assert.Same(original, fixture.Host.RadarSettingsWindow);
        Assert.True(original.IsVisible);
        Assert.False(original.IsClosed);
        Assert.Equal(1, fixture.Host.CreateSettingsCalls);
    }

    [Fact]
    public void CameraReturnAndCancel_RestoresSameCameraSettingsWindow()
    {
        var fixture = Fixture.Create(
            launchMinimized: false,
            activeProviderId: "blaze.camera.vision");
        fixture.Coordinator.Start(new BridgeSettings(1, "blaze.camera.vision"));
        var original = fixture.CameraWindow;

        Assert.Single(fixture.Windows.Headers).ReturnToSelectionCommand.Execute(null);
        Assert.Single(fixture.Windows.SelectorWindows).ViewModel.CancelCommand.Execute(null);

        Assert.Same(original, fixture.CameraWindow);
        Assert.True(original.IsVisible);
        Assert.False(original.IsClosed);
        Assert.Equal(1, fixture.Host.CreateSettingsCalls);
    }

    [Fact]
    public async Task ConfirmedSwitch_ClosesOldWindowWithoutRequestingShutdown()
    {
        var fixture = Fixture.Create(
            launchMinimized: false,
            activeProviderId: "blaze.radar.f10f20");
        fixture.Coordinator.Start(new BridgeSettings(1, "blaze.radar.f10f20"));
        Assert.Single(fixture.Windows.Headers).ReturnToSelectionCommand.Execute(null);
        var selector = Assert.Single(fixture.Windows.SelectorWindows).ViewModel;
        selector.SelectedChoice = selector.Choices.Single(choice =>
            choice.ProviderId == "blaze.camera.vision");

        selector.ConfirmCommand.Execute(null);
        await WaitUntilAsync(() => fixture.CameraWindow.IsVisible);

        Assert.True(fixture.RadarWindow.IsClosed);
        Assert.False(fixture.CameraWindow.IsClosed);
        Assert.Equal(0, fixture.ShutdownRequests);
        Assert.Equal("blaze.camera.vision", fixture.Host.ActiveProviderId);
    }

    [Fact]
    public void UserClosingProviderWindow_RequestsBridgeShutdown()
    {
        var fixture = Fixture.Create(
            launchMinimized: false,
            activeProviderId: "blaze.radar.f10f20");
        fixture.Coordinator.Start(new BridgeSettings(1, "blaze.radar.f10f20"));

        fixture.RadarWindow.Close();

        Assert.Equal(1, fixture.ShutdownRequests);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Fixture
    {
        private Fixture(bool launchMinimized, string? activeProviderId)
        {
            RadarWindow = new FakeWindowAdapter("radar-content");
            CameraWindow = new FakeWindowAdapter("camera-content");
            Host = new FakeWindowHost(activeProviderId, RadarWindow, CameraWindow);
            Windows = new FakeWindowFactory();
            Coordinator = new BridgeWindowCoordinator(
                Host,
                EmptySettingsContext.Instance,
                Windows,
                new ImmediateDispatcher(),
                launchMinimized,
                () => ShutdownRequests++);
        }

        internal FakeWindowHost Host { get; }
        internal FakeWindowFactory Windows { get; }
        internal FakeWindowAdapter RadarWindow { get; }
        internal FakeWindowAdapter CameraWindow { get; }
        internal BridgeWindowCoordinator Coordinator { get; }
        internal int ShutdownRequests { get; private set; }

        internal static Fixture Create(bool launchMinimized, string? activeProviderId) =>
            new(launchMinimized, activeProviderId);
    }

    private sealed class FakeWindowHost : IBridgeWindowHost
    {
        private readonly IReadOnlyDictionary<string, FakeWindowAdapter> _windows;
        private BridgeHostSnapshot _snapshot;

        internal FakeWindowHost(
            string? activeProviderId,
            FakeWindowAdapter radar,
            FakeWindowAdapter camera)
        {
            AvailableProviders =
            [
                new BridgeAvailableProvider(
                    "blaze.radar.f10f20", "radar-main", "Radar", "Radar", true),
                new BridgeAvailableProvider(
                    "blaze.camera.vision", "camera-main", "Camera", "Camera", true)
            ];
            _windows = new Dictionary<string, FakeWindowAdapter>
            {
                ["blaze.radar.f10f20"] = radar,
                ["blaze.camera.vision"] = camera
            };
            ActiveProviderId = activeProviderId;
            _snapshot = Snapshot(activeProviderId);
        }

        public IReadOnlyList<BridgeAvailableProvider> AvailableProviders { get; }
        public BridgeHostSnapshot CurrentSnapshot => _snapshot;
        public string? ActiveProviderId { get; private set; }
        public int SelectCalls { get; private set; }
        public int CreateSettingsCalls { get; private set; }
        public FakeWindowAdapter RadarSettingsWindow => _windows["blaze.radar.f10f20"];
        public event EventHandler? ActiveProviderChanged;
        public event Action<BridgeHostSnapshot>? SnapshotChanged;

        public Task SelectProviderAsync(string providerId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SelectCalls++;
            ActiveProviderId = providerId;
            _snapshot = Snapshot(providerId);
            ActiveProviderChanged?.Invoke(this, EventArgs.Empty);
            SnapshotChanged?.Invoke(_snapshot);
            return Task.CompletedTask;
        }

        public object? CreateActiveProviderSettingsView(IProviderSettingsContext context)
        {
            CreateSettingsCalls++;
            return ActiveProviderId is null ? null : _windows[ActiveProviderId];
        }

        internal void RepublishActiveProvider() =>
            ActiveProviderChanged?.Invoke(this, EventArgs.Empty);

        private BridgeHostSnapshot Snapshot(string? providerId)
        {
            var provider = AvailableProviders.FirstOrDefault(item => item.ProviderId == providerId);
            return new BridgeHostSnapshot(
                InteractionHostStatus.Disconnected,
                provider,
                provider is null ? null : ProviderRuntimeStatus.Running);
        }
    }

    private sealed class FakeWindowFactory : IBridgeWindowFactory
    {
        internal List<(FakeWindowAdapter Window, ProviderSelectorViewModel ViewModel)> SelectorWindows { get; } = [];
        internal List<ProviderHeaderViewModel> Headers { get; } = [];
        internal int WrapCalls { get; private set; }

        public IBridgeWindowAdapter CreateSelectorWindow(ProviderSelectorViewModel viewModel)
        {
            var window = new FakeWindowAdapter(viewModel);
            SelectorWindows.Add((window, viewModel));
            return window;
        }

        public IBridgeWindowAdapter AdaptProviderWindow(object providerView) =>
            Assert.IsType<FakeWindowAdapter>(providerView);

        public object CreateProviderHeader(ProviderHeaderViewModel viewModel)
        {
            Headers.Add(viewModel);
            return viewModel;
        }

        public object WrapProviderContent(object header, object? originalContent)
        {
            WrapCalls++;
            return new FakeWrappedContent(header, originalContent);
        }
    }

    private sealed record FakeWrappedContent(object Header, object? Original);

    private sealed class FakeWindowAdapter(object? content) : IBridgeWindowAdapter
    {
        public object? Content { get; set; } = content;
        public bool IsVisible { get; private set; }
        public bool IsMinimized { get; set; }
        public bool IsClosed { get; private set; }
        public event EventHandler? Closed;

        public void Show()
        {
            IsVisible = true;
            IsClosed = false;
        }

        public void Hide() => IsVisible = false;

        public void Close()
        {
            IsVisible = false;
            IsClosed = true;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class ImmediateDispatcher : IBridgeUiDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class EmptySettingsContext : IProviderSettingsContext
    {
        internal static EmptySettingsContext Instance { get; } = new();
        public ValueTask<string?> GetValueAsync(string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);
        public ValueTask SetValueAsync(string key, string? value, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
