using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Ipc;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarBridgeRuntimeTests
{
    [Fact]
    public async Task StartInfrastructureAsync_CanceledFirstAttemptCanBeRetried()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        await using var runtime = new RadarBridgeRuntime(
            configuration,
            NullLogger<RadarBridgeRuntime>.Instance);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.StartInfrastructureAsync(canceled.Token));
        await runtime.StartInfrastructureAsync();

        var pipeTaskField = typeof(RadarBridgeRuntime).GetField(
            "_pipeTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pipeTaskField?.GetValue(runtime));
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMoreThanOnce()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        var runtime = new RadarBridgeRuntime(
            configuration,
            NullLogger<RadarBridgeRuntime>.Instance);

        await runtime.StartInfrastructureAsync();
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Simulation_ProducesProcessedSnapshotsAndStopsCooperatively()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        configuration.Range.ActivePolygon =
        [
            new(-5f, -5f),
            new(5f, -5f),
            new(5f, 5f),
            new(-5f, 5f)
        ];
        await using var runtime = new RadarBridgeRuntime(
            configuration,
            NullLogger<RadarBridgeRuntime>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<RadarRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.SnapshotUpdated += snapshot => received.TrySetResult(snapshot);

        await runtime.StartInfrastructureAsync(cancellation.Token);
        await runtime.StartSimulationAsync(cancellation.Token);
        var snapshot = await received.Task.WaitAsync(cancellation.Token);
        await runtime.StopSimulationAsync();

        Assert.NotEmpty(snapshot.RawPoints);
        Assert.NotEmpty(snapshot.ValidPoints);
        Assert.True(snapshot.Sequence > 0);
        Assert.InRange(snapshot.ScanFrequencyHz, 1, 30);
    }

    [Fact]
    public async Task Simulation_FaultingSnapshotSubscriber_DoesNotStopOtherSubscribers()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        configuration.Range.ActivePolygon =
        [
            new(-5f, -5f),
            new(5f, -5f),
            new(5f, 5f),
            new(-5f, 5f)
        ];
        await using var runtime = new RadarBridgeRuntime(
            configuration,
            NullLogger<RadarBridgeRuntime>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<RadarRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.SnapshotUpdated += _ => throw new InvalidOperationException("observer failed");
        runtime.SnapshotUpdated += snapshot => received.TrySetResult(snapshot);

        await runtime.StartInfrastructureAsync(cancellation.Token);
        await runtime.StartSimulationAsync(cancellation.Token);
        var completed = await Task.WhenAny(received.Task, Task.Delay(500, cancellation.Token));
        await runtime.StopSimulationAsync();

        Assert.Same(received.Task, completed);
    }

    [Fact]
    public async Task ConcurrentSimulationStarts_StopWithoutLeavingOrphanProducers()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        configuration.Range.ActivePolygon =
        [
            new(-5f, -5f),
            new(5f, -5f),
            new(5f, 5f),
            new(-5f, 5f)
        ];
        await using var runtime = new RadarBridgeRuntime(
            configuration,
            NullLogger<RadarBridgeRuntime>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var snapshotCount = 0;
        runtime.SnapshotUpdated += _ => Interlocked.Increment(ref snapshotCount);

        await runtime.StartInfrastructureAsync(cancellation.Token);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(
            _ => Task.Run(() => runtime.StartSimulationAsync(cancellation.Token), cancellation.Token)));
        await runtime.StopSimulationAsync();
        await Task.Delay(150, cancellation.Token);
        var stoppedCount = Volatile.Read(ref snapshotCount);
        await Task.Delay(250, cancellation.Token);

        Assert.Equal(stoppedCount, Volatile.Read(ref snapshotCount));
    }

    [Fact]
    public async Task ReplayAsync_MissingFileFailsAtOperationBoundary()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        await using var runtime = new RadarBridgeRuntime(
            configuration,
            NullLogger<RadarBridgeRuntime>.Instance);
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".radarrec");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => runtime.ReplayAsync(missingPath, 1d, loop: false));
    }

    [Fact]
    public async Task Simulation_MapsActiveRegionToUnityWithoutExplicitCalibration()
    {
        var configuration = CreateIpcSimulationConfiguration();
        await using var runtime = new RadarBridgeRuntime(
            configuration,
            NullLogger<RadarBridgeRuntime>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await runtime.StartInfrastructureAsync(cancellation.Token);
        await using var client = await ConnectUnityClientAsync(configuration.Ipc.PipeName, cancellation.Token);
        await runtime.StartSimulationAsync(cancellation.Token);

        var batch = await ReadPointerBatchAsync(client, requirePointers: true, cancellation.Token);
        var frame = Assert.Single(batch.Screens);

        Assert.NotEmpty(frame.Pointers);
        Assert.Equal("main", frame.Screen.ScreenId);
        Assert.All(frame.Pointers, pointer =>
        {
            Assert.InRange(pointer.NormalizedX, 0f, 1f);
            Assert.InRange(pointer.NormalizedY, 0f, 1f);
            Assert.Equal(pointer.NormalizedX * frame.Screen.WidthPixels, pointer.PixelX);
            Assert.Equal(pointer.NormalizedY * frame.Screen.HeightPixels, pointer.PixelY);
        });
    }

    [Fact]
    public async Task Simulation_SendsEmptyPointerBatchesWhenFiltersRejectEveryPoint()
    {
        var configuration = CreateIpcSimulationConfiguration();
        configuration.Range.ActivePolygon =
        [
            new(4f, 5f),
            new(5f, 5f),
            new(5f, 4f),
            new(4f, 4f)
        ];
        await using var runtime = new RadarBridgeRuntime(
            configuration,
            NullLogger<RadarBridgeRuntime>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await runtime.StartInfrastructureAsync(cancellation.Token);
        await using var client = await ConnectUnityClientAsync(configuration.Ipc.PipeName, cancellation.Token);
        await runtime.StartSimulationAsync(cancellation.Token);

        var batch = await ReadPointerBatchAsync(client, requirePointers: false, cancellation.Token);
        var frame = Assert.Single(batch.Screens);

        Assert.Empty(frame.Pointers);
    }

    private static RadarAppConfiguration CreateIpcSimulationConfiguration()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        configuration.Range.ActivePolygon =
        [
            new(-5f, 5f),
            new(5f, 5f),
            new(5f, -5f),
            new(-5f, -5f)
        ];
        return configuration;
    }

    private static async Task<NamedPipeClientStream> ConnectUnityClientAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellationToken);
        await IpcStream.WriteAsync(
            client,
            IpcEnvelope.Create(
                IpcMessageType.Hello,
                1,
                new HelloPayload(
                    42,
                    "2021.3",
                    [new RadarScreenDefinitionPayload("main", "Main", 1920, 1080, true, 0)])),
            cancellationToken);
        var acknowledgement = await IpcStream.ReadAsync(client, cancellationToken);
        Assert.Equal(IpcMessageType.HelloAck, acknowledgement.MessageType);
        return client;
    }

    private static async Task<PointerBatchPayload> ReadPointerBatchAsync(
        Stream client,
        bool requirePointers,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var envelope = await IpcStream.ReadAsync(client, cancellationToken);
            Assert.NotEqual(IpcMessageType.PointerFrame, envelope.MessageType);
            if (envelope.MessageType != IpcMessageType.PointerBatch)
            {
                continue;
            }

            var batch = envelope.DeserializePayload<PointerBatchPayload>();
            if (!requirePointers || Assert.Single(batch.Screens).Pointers.Count > 0)
            {
                return batch;
            }
        }
    }
}
