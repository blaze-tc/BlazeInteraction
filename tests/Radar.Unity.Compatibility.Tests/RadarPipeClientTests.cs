using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Blaze.Interaction;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Radar.Unity.Compatibility.Tests;

public sealed class RadarPipeClientTests
{
    [Fact]
    public async Task Client_ColdStartConnectionTimeoutsRemainPendingUntilBridgeBecomesAvailable()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = new InteractionPipeClient(pipeName, 75, 25, 2500);
        var errors = new List<string>();
        client.ErrorReceived += errors.Add;

        client.Start(Hello("main"));
        await Task.Delay(250, timeout.Token);
        client.DrainMainThreadEvents();

        Assert.Empty(errors);
        Assert.Equal(string.Empty, client.LastError);

        await using var server = Server(pipeName);
        await server.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(server, timeout.Token);
        await WriteEnvelopeAsync(server, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        client.DrainMainThreadEvents();

        Assert.True(client.IsConnected);
        Assert.Empty(errors);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_ReconnectWaitDoesNotRepeatNamedPipeTimeoutErrors()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = new InteractionPipeClient(pipeName, 75, 25, 2500);
        var errors = new List<string>();
        client.ErrorReceived += errors.Add;

        await using (var server = Server(pipeName))
        {
            await ConnectAndAckAsync(server, client, timeout.Token);
        }

        await WaitUntilAsync(() => !client.IsConnected, timeout.Token);
        await Task.Delay(300, timeout.Token);
        client.DrainMainThreadEvents();

        Assert.DoesNotContain(errors, value => value.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(errors.Count, 1, 1);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_RepeatedServerRejectionIsReportedOnceAcrossReconnectAttempts()
    {
        const string rejection = "Configuration was rejected.";
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);
        var errors = new List<string>();
        client.ErrorReceived += errors.Add;
        client.Start(Hello("main"));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using (var server = Server(pipeName))
            {
                await server.WaitForConnectionAsync(timeout.Token);
                await ReadEnvelopeAsync(server, timeout.Token);
                await WriteEnvelopeAsync(
                    server,
                    InteractionIpcProtocol.Create(
                        InteractionMessageType.Error,
                        attempt + 1,
                        new ErrorPayload { Code = "configuration_rejected", Message = rejection }),
                    timeout.Token);
                await Task.Delay(75, timeout.Token);
            }

            await Task.Delay(75, timeout.Token);
            client.DrainMainThreadEvents();
        }

        Assert.Equal(1, errors.Count(value => value == rejection));
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_UnityStallPreservesDownMoveUpAndFollowingEmptyFrameInOrder()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        await ConnectAndAckAsync(server, client, timeout.Token);

        await WriteEnvelopesAsync(
            server,
            [
                Envelope(Frame(10, InteractionPhase.Down)),
                Envelope(Frame(11, InteractionPhase.Move)),
                Envelope(Frame(12, InteractionPhase.Up)),
                Envelope(Frame(13, null))
            ],
            timeout.Token,
            fragment: false);
        await WaitUntilAsync(() => client.DroppedFrameCount == 0, timeout.Token, minimumDelayMilliseconds: 100);

        var received = new List<InteractionFrame>();
        while (client.TryConsumeLatestFrame(out var frame)) received.Add(frame);

        Assert.Equal([10L, 11L, 12L, 13L], received.Select(frame => frame.Sequence));
        Assert.Equal(
            [InteractionPhase.Down, InteractionPhase.Move, InteractionPhase.Up],
            received.Take(3).Select(frame => frame.Points.Single().Phase));
        Assert.Empty(received[3].Points);
        await client.StopAsync();
    }

    [Fact]
    public void Protocol_CreateUsesInteractionIpcOne()
    {
        var hello = InteractionIpcProtocol.Create(InteractionMessageType.Hello, 1, Hello("main"));

        Assert.Equal(1, InteractionIpcProtocol.Version);
        Assert.Equal(1, hello.ProtocolVersion);
        Assert.Equal(InteractionMessageType.Hello, hello.MessageType);
    }

    [Fact]
    public async Task Client_HelloContainsEveryCallerSurfaceAndNoLegacyResolutionFields()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);

        client.Start(Hello("left", "front", "right"));
        await server.WaitForConnectionAsync(timeout.Token);
        var received = await ReadEnvelopeWithJsonAsync(server, timeout.Token);
        var payload = JObject.Parse(received.Json)["payload"]!;

        Assert.Equal(InteractionMessageType.Hello, received.Envelope.MessageType);
        Assert.Contains("\"protocolVersion\":1", received.Json, StringComparison.Ordinal);
        Assert.Equal(
            new[] { "left", "front", "right" },
            received.Envelope.DeserializePayload<HelloPayload>().Surfaces.Select(surface => surface.SurfaceId));
        Assert.Null(payload["screenWidth"]);
        Assert.Null(payload["screenHeight"]);
        Assert.Equal(3, payload["surfaces"]!.Count());

        await WriteEnvelopeAsync(server, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_HelloAckGatesConnectionAndRetainsProviderFields()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        var callbackThread = -1;
        var callbackConnected = false;
        client.ConnectionChanged += _ => throw new InvalidOperationException("observer failed");
        client.ConnectionChanged += connected =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            callbackConnected = connected;
        };

        client.Start(Hello("front"));
        await server.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(server, timeout.Token);
        Assert.False(client.IsConnected);

        await WriteEnvelopeAsync(server, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);

        Assert.False(callbackConnected);
        var drainThread = Environment.CurrentManagedThreadId;
        client.DrainMainThreadEvents();
        Assert.True(callbackConnected);
        Assert.Equal(drainThread, callbackThread);
        Assert.Equal("1.0.0", client.BridgeVersion);
        Assert.Equal("blaze.radar.f10f20", client.ActiveProvider!.Id);
        Assert.Equal("radar-main", client.ActiveProvider.InstanceId);

        await client.StopAsync();
    }

    [Fact]
    public async Task Client_FragmentedStickyAckAndFrameRoundTripAtomically()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        client.Start(Hello("front"));
        await server.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(server, timeout.Token);

        var expected = Frame(42, InteractionPhase.Move, "front");
        await WriteEnvelopesAsync(server, [Ack(), Envelope(expected)], timeout.Token, fragment: true);
        var received = await ConsumeFrameAsync(client, timeout.Token);

        Assert.Equal(42, received.Sequence);
        Assert.Equal("front", received.SurfaceId);
        var point = Assert.Single(received.Points);
        Assert.Equal(7, point.Id);
        Assert.Equal(2048f, point.PixelPosition!.X);
        Assert.Equal(384f, point.PixelPosition.Y);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_TwoUnconsumedVisualFramesDropOneAndLatestWins()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        await ConnectAndAckAsync(server, client, timeout.Token);

        await WriteEnvelopesAsync(
            server,
            [Envelope(Frame(1, InteractionPhase.Move)), Envelope(Frame(2, InteractionPhase.Move))],
            timeout.Token,
            fragment: false);
        await WaitUntilAsync(() => client.DroppedFrameCount == 1, timeout.Token);

        Assert.True(client.TryConsumeLatestFrame(out var latest));
        Assert.Equal(2, latest.Sequence);
        Assert.Equal(1, client.DroppedFrameCount);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_FrameBeforeAckIsRejectedAndSessionReconnects()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);

        await using (var firstServer = Server(pipeName))
        {
            client.Start(Hello("main"));
            await firstServer.WaitForConnectionAsync(timeout.Token);
            await ReadEnvelopeAsync(firstServer, timeout.Token);
            await WriteEnvelopeAsync(firstServer, Envelope(Frame(2, InteractionPhase.Down)), timeout.Token);
            await WaitUntilAsync(
                () => client.LastError.Contains("before HelloAck", StringComparison.OrdinalIgnoreCase),
                timeout.Token);
        }

        Assert.False(client.IsConnected);
        Assert.False(client.TryConsumeLatestFrame(out _));
        await using var secondServer = Server(pipeName);
        await secondServer.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(secondServer, timeout.Token);
        await WriteEnvelopeAsync(secondServer, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        await client.StopAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task Client_ProtocolMismatchReportsErrorReconnectsAndNeverPublishesRejectedData(int protocolVersion)
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);

        await using (var firstServer = Server(pipeName))
        {
            client.Start(Hello("main"));
            await firstServer.WaitForConnectionAsync(timeout.Token);
            await ReadEnvelopeAsync(firstServer, timeout.Token);
            var invalid = Envelope(Frame(1, InteractionPhase.Down));
            invalid.ProtocolVersion = protocolVersion;
            await WriteEnvelopeAsync(firstServer, invalid, timeout.Token);
            await WaitUntilAsync(
                () => client.LastError.Contains(protocolVersion.ToString(), StringComparison.Ordinal),
                timeout.Token);
        }

        Assert.False(client.TryConsumeLatestFrame(out _));
        await using var secondServer = Server(pipeName);
        await secondServer.WaitForConnectionAsync(timeout.Token);
        Assert.Equal(InteractionMessageType.Hello, (await ReadEnvelopeAsync(secondServer, timeout.Token)).MessageType);
        await WriteEnvelopeAsync(secondServer, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_MalformedJsonReportsErrorAndReconnectsInsteadOfEndingRunLoop()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);

        await using (var firstServer = Server(pipeName))
        {
            client.Start(Hello("main"));
            await firstServer.WaitForConnectionAsync(timeout.Token);
            await ReadEnvelopeAsync(firstServer, timeout.Token);
            await WriteRawJsonAsync(firstServer, "{not-json", timeout.Token);
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(client.LastError), timeout.Token);
        }

        await using var secondServer = Server(pipeName);
        await secondServer.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(secondServer, timeout.Token);
        await WriteEnvelopeAsync(secondServer, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_NullFramePayloadReportsErrorReconnectsAndPublishesNothing()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);

        await using (var firstServer = Server(pipeName))
        {
            await ConnectAndAckAsync(firstServer, client, timeout.Token);
            var invalid = new InteractionEnvelope
            {
                ProtocolVersion = InteractionIpcProtocol.Version,
                MessageType = InteractionMessageType.InteractionFrame,
                Sequence = 10,
                Payload = JValue.CreateNull()
            };
            await WriteEnvelopeAsync(firstServer, invalid, timeout.Token);
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(client.LastError), timeout.Token);
        }

        Assert.False(client.TryConsumeLatestFrame(out _));
        await using var secondServer = Server(pipeName);
        await secondServer.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(secondServer, timeout.Token);
        await WriteEnvelopeAsync(secondServer, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_DuplicateAckReportsErrorAndReconnects()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);

        await using (var firstServer = Server(pipeName))
        {
            await ConnectAndAckAsync(firstServer, client, timeout.Token);
            await WriteEnvelopeAsync(firstServer, Ack(), timeout.Token);
            await WaitUntilAsync(
                () => client.LastError.Contains("duplicate HelloAck", StringComparison.OrdinalIgnoreCase),
                timeout.Token);
        }

        await using var secondServer = Server(pipeName);
        await secondServer.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(secondServer, timeout.Token);
        await WriteEnvelopeAsync(secondServer, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_ProviderChangedUpdatesStateAndRaisesOnDrainThread()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        await ConnectAndAckAsync(server, client, timeout.Token);
        ProviderChangedPayload? observed = null;
        client.ProviderChangedReceived += change => observed = change;

        var change = new ProviderChangedPayload
        {
            PreviousProvider = new ProviderReferencePayload { Id = "blaze.radar.f10f20", InstanceId = "radar-main" },
            ActiveProvider = new ProviderReferencePayload { Id = "blaze.test", InstanceId = "test-main" },
            TimestampUnixMs = 5000
        };
        await WriteEnvelopeAsync(
            server,
            InteractionIpcProtocol.Create(InteractionMessageType.ProviderChanged, 5, change),
            timeout.Token);
        await WaitUntilAsync(() => client.ActiveProvider?.Id == "blaze.test", timeout.Token);
        Assert.Null(observed);
        client.DrainMainThreadEvents();

        Assert.Equal("blaze.test", observed!.ActiveProvider!.Id);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_StatusRaisesOnDrainThreadAndIsolatesObservers()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        await ConnectAndAckAsync(server, client, timeout.Token);
        StatusPayload? observed = null;
        client.StatusReceived += _ => throw new InvalidOperationException("observer failed");
        client.StatusReceived += status => observed = status;

        var status = new StatusPayload
        {
            Code = "provider_status",
            State = "Running",
            Message = "ready",
            Provider = new ProviderReferencePayload { Id = "blaze.radar.f10f20", InstanceId = "radar-main" },
            TimestampUnixMs = 5000
        };
        await WriteEnvelopeAsync(
            server,
            InteractionIpcProtocol.Create(InteractionMessageType.Status, 6, status),
            timeout.Token);
        await Task.Delay(75, timeout.Token);
        Assert.Null(observed);
        client.DrainMainThreadEvents();

        Assert.Equal("ready", observed!.Message);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_HeartbeatRemainsLengthPrefixedPing()
    {
        var pipeName = PipeName();
        using var timeout = Timeout(seconds: 7);
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        await ConnectAndAckAsync(server, client, timeout.Token);

        var heartbeat = await ReadEnvelopeAsync(server, timeout.Token);
        Assert.Equal(InteractionMessageType.Ping, heartbeat.MessageType);
        Assert.True(heartbeat.DeserializePayload<PingPayload>().TimestampUnixMs > 0);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_RepeatedLifecycleAndDisconnectThenReconnectAreDeterministic()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);

        await client.StopAsync();
        await client.StopAsync();
        await using (var firstServer = Server(pipeName))
        {
            await ConnectAndAckAsync(firstServer, client, timeout.Token);
            client.Start(Hello("ignored-overlap"));
            await client.StopAsync();
            await client.StopAsync();
        }

        await using (var secondServer = Server(pipeName))
        {
            await ConnectAndAckAsync(secondServer, client, timeout.Token);
            Assert.True(client.IsConnected);
            await client.StopAsync();
        }

        Assert.False(client.IsConnected);
        Assert.False(client.TryConsumeLatestFrame(out _));
    }

    [Fact]
    public async Task Client_StartDuringInProgressStopDoesNotCreateOverlappingLoop()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);
        await using (var server = Server(pipeName))
        {
            client.Start(Hello("main"));
            await server.WaitForConnectionAsync(timeout.Token);
            await ReadEnvelopeAsync(server, timeout.Token);
            var stopping = client.StopAsync();
            client.Start(Hello("overlap"));
            await stopping;
        }

        await using var probe = Server(pipeName);
        using var noConnection = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitForConnectionAsync(noConnection.Token));
        Assert.False(client.IsConnected);
    }

    private static InteractionPipeClient Client(string pipeName) => new(pipeName, 500, 50, 2500);

    private static NamedPipeServerStream Server(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private static CancellationTokenSource Timeout(int seconds = 5) =>
        new(TimeSpan.FromSeconds(seconds));

    private static string PipeName() => "BlazeInteraction.Tests." + Guid.NewGuid().ToString("N");

    private static HelloPayload Hello(params string[] surfaceIds) => new()
    {
        UnityPid = Environment.ProcessId,
        UnityVersion = "2021.3.45f1",
        SdkVersion = "1.0.0",
        Surfaces = surfaceIds.Select((id, index) => new InteractionSurface
        {
            SurfaceId = id,
            Name = id,
            LogicalWidth = 1920 + index,
            LogicalHeight = 1080,
            IsPrimary = index == 0,
            Order = index
        }).ToList()
    };

    private static InteractionEnvelope Ack() => InteractionIpcProtocol.Create(
        InteractionMessageType.HelloAck,
        1,
        new HelloAckPayload
        {
            BridgeVersion = "1.0.0",
            ActiveProvider = new ProviderReferencePayload
            {
                Id = "blaze.radar.f10f20",
                InstanceId = "radar-main"
            },
            Capabilities = ["radar"]
        });

    private static InteractionFrame Frame(
        long sequence,
        InteractionPhase? phase,
        string surfaceId = "main") => new()
    {
        ProviderId = "blaze.radar.f10f20",
        ProviderInstanceId = "radar-main",
        SurfaceId = surfaceId,
        Sequence = sequence,
        TimestampUnixMs = 1000 + sequence,
        Points = phase.HasValue
            ? [new InteractionPoint
            {
                Id = 7,
                SurfaceId = surfaceId,
                ProviderId = "blaze.radar.f10f20",
                ProviderInstanceId = "radar-main",
                SourceId = "radar-fused-output",
                Phase = phase.Value,
                NormalizedPosition = new Vector2Data { X = .5f, Y = .25f },
                PixelPosition = new Vector2Data { X = 2048f, Y = 384f },
                Confidence = .9f,
                TimestampUnixMs = 1000 + sequence
            }]
            : []
    };

    private static InteractionEnvelope Envelope(InteractionFrame frame) =>
        InteractionIpcProtocol.Create(InteractionMessageType.InteractionFrame, frame.Sequence, frame);

    private static async Task ConnectAndAckAsync(
        NamedPipeServerStream server,
        InteractionPipeClient client,
        CancellationToken cancellationToken)
    {
        client.Start(Hello("main"));
        await server.WaitForConnectionAsync(cancellationToken);
        Assert.Equal(InteractionMessageType.Hello, (await ReadEnvelopeAsync(server, cancellationToken)).MessageType);
        await WriteEnvelopeAsync(server, Ack(), cancellationToken);
        await WaitUntilAsync(() => client.IsConnected, cancellationToken);
    }

    private static async Task<InteractionFrame> ConsumeFrameAsync(
        InteractionPipeClient client,
        CancellationToken cancellationToken)
    {
        InteractionFrame? frame = null;
        await WaitUntilAsync(() => client.TryConsumeLatestFrame(out frame), cancellationToken);
        return frame!;
    }

    private static async Task<InteractionEnvelope> ReadEnvelopeAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken) =>
        (await ReadEnvelopeWithJsonAsync(server, cancellationToken)).Envelope;

    private static async Task<(InteractionEnvelope Envelope, string Json)> ReadEnvelopeWithJsonAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(server, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var payload = new byte[length];
        await ReadExactlyAsync(server, payload, cancellationToken);
        var json = Encoding.UTF8.GetString(payload);
        return (JsonConvert.DeserializeObject<InteractionEnvelope>(json)!, json);
    }

    private static async Task WriteEnvelopeAsync(
        NamedPipeServerStream server,
        InteractionEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var frame = EncodeEnvelope(envelope);
        await server.WriteAsync(frame, cancellationToken);
        await server.FlushAsync(cancellationToken);
    }

    private static async Task WriteRawJsonAsync(
        NamedPipeServerStream server,
        string json,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await server.WriteAsync(frame, cancellationToken);
        await server.FlushAsync(cancellationToken);
    }

    private static async Task WriteEnvelopesAsync(
        NamedPipeServerStream server,
        IReadOnlyList<InteractionEnvelope> envelopes,
        CancellationToken cancellationToken,
        bool fragment)
    {
        var bytes = envelopes.SelectMany(EncodeEnvelope).ToArray();
        if (!fragment)
        {
            await server.WriteAsync(bytes, cancellationToken);
            await server.FlushAsync(cancellationToken);
            return;
        }

        var split = Math.Min(7, bytes.Length - 1);
        await server.WriteAsync(bytes.AsMemory(0, split), cancellationToken);
        await server.FlushAsync(cancellationToken);
        await Task.Delay(10, cancellationToken);
        await server.WriteAsync(bytes.AsMemory(split), cancellationToken);
        await server.FlushAsync(cancellationToken);
    }

    private static byte[] EncodeEnvelope(InteractionEnvelope envelope)
    {
        var payload = Encoding.UTF8.GetBytes(InteractionIpcProtocol.Serialize(envelope));
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        CancellationToken cancellationToken,
        int minimumDelayMilliseconds = 0)
    {
        if (minimumDelayMilliseconds > 0)
        {
            await Task.Delay(minimumDelayMilliseconds, cancellationToken);
        }

        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }
}
