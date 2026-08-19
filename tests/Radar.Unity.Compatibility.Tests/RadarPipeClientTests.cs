using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Blaze.Radar;
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
        using var client = new RadarPipeClient(pipeName, 75, 25, 2500);
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
        using var client = new RadarPipeClient(pipeName, 75, 25, 2500);
        var errors = new List<string>();
        client.ErrorReceived += errors.Add;

        await using (var server = Server(pipeName))
        {
            await ConnectAndAckAsync(server, client, timeout.Token);
        }

        await WaitUntilAsync(() => !client.IsConnected, timeout.Token);
        await Task.Delay(300, timeout.Token);
        client.DrainMainThreadEvents();

        Assert.DoesNotContain(errors, value =>
            value.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(errors.Count, 1, 1);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_RepeatedServerRejectionIsReportedOnceAcrossReconnectAttempts()
    {
        const string rejection =
            "Configuration was rejected during loading and cannot be reconciled or saved. " +
            "Create a new configuration explicitly first.";
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = new RadarPipeClient(pipeName, 500, 25, 2500);
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
                    RadarIpcProtocol.Create(
                        RadarIpcMessageType.Error,
                        attempt + 1,
                        new RadarErrorPayload { code = "configuration_rejected", message = rejection }),
                    timeout.Token);
                await Task.Delay(75, timeout.Token);
            }

            await Task.Delay(75, timeout.Token);
            client.DrainMainThreadEvents();
        }

        Assert.Equal(1, errors.Count(value => value == rejection));
        Assert.InRange(
            errors.Count(value => value.Contains("closed the Named Pipe", StringComparison.OrdinalIgnoreCase)),
            0,
            1);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_UnityStallPreservesDownMoveUpAndFollowingZeroBatchInOrder()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        await ConnectAndAckAsync(server, client, timeout.Token);

        await WriteFragmentedEnvelopesAsync(
            server,
            new[]
            {
                RadarIpcProtocol.Create(RadarIpcMessageType.PointerBatch, 10, Batch(Frame("main", 10, Pointer(7, 100, 100, .1f, .1f, 1010, RadarPointerPhase.Down)))),
                RadarIpcProtocol.Create(RadarIpcMessageType.PointerBatch, 11, Batch(Frame("main", 11, Pointer(7, 200, 200, .2f, .2f, 1011, RadarPointerPhase.Move)))),
                RadarIpcProtocol.Create(RadarIpcMessageType.PointerBatch, 12, Batch(Frame("main", 12, Pointer(7, 200, 200, .2f, .2f, 1012, RadarPointerPhase.Up)))),
                RadarIpcProtocol.Create(RadarIpcMessageType.PointerBatch, 13, Batch(Frame("main", 13)))
            },
            timeout.Token,
            fragment: false);
        await Task.Delay(100, timeout.Token);

        var received = new List<RadarPointerBatchPayload>();
        while (client.TryConsumeLatestBatch(out var batch)) received.Add(batch);

        Assert.Equal([10L, 11L, 12L, 13L], received.Select(batch => batch.screens.Single().sequence));
        Assert.Equal(
            [RadarPointerPhase.Down, RadarPointerPhase.Move, RadarPointerPhase.Up],
            received.Take(3).Select(batch => batch.screens.Single().pointers.Single().phase));
        Assert.Empty(received[3].screens.Single().pointers);
        await client.StopAsync();
    }

    [Fact]
    public void Protocol_CreateUsesV2AndRejectsLegacyPointerFrame()
    {
        var hello = RadarIpcProtocol.Create(RadarIpcMessageType.Hello, 1, Hello("main"));

        Assert.Equal(2, RadarIpcProtocol.Version);
        Assert.Equal(2, hello.protocolVersion);
        var error = Assert.Throws<InvalidOperationException>(() =>
            RadarIpcProtocol.Create(RadarIpcMessageType.PointerFrame, 2, new RadarPointerFrameMessage()));
        Assert.Contains("protocol v2", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Client_HelloContainsEveryCallerScreenAndNoV1ResolutionFields()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);

        client.Start(Hello("left", "front", "right"));
        await server.WaitForConnectionAsync(timeout.Token);
        var received = await ReadEnvelopeWithJsonAsync(server, timeout.Token);
        var payload = JObject.Parse(received.Json)["payload"]!;

        Assert.Equal(RadarIpcMessageType.Hello, received.Envelope.messageType);
        Assert.Contains("\"protocolVersion\":2", received.Json, StringComparison.Ordinal);
        Assert.Contains("\"messageType\":\"Hello\"", received.Json, StringComparison.Ordinal);
        Assert.Equal(new[] { "left", "front", "right" },
            received.Envelope.payload.ToObject<RadarHelloPayload>()!.screens.Select(screen => screen.screenId));
        Assert.Null(payload["screenWidth"]);
        Assert.Null(payload["screenHeight"]);
        Assert.Equal(3, payload["screens"]!.Count());

        await WriteEnvelopeAsync(server, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_HelloAckGatesConnectionAndRetainsEveryV2Field()
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

        await WriteEnvelopeAsync(server, Ack(connected: false), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);

        Assert.False(callbackConnected);
        var drainThread = Environment.CurrentManagedThreadId;
        client.DrainMainThreadEvents();
        Assert.True(callbackConnected);
        Assert.Equal(drainThread, callbackThread);
        Assert.True(client.IsConnected);
        Assert.Equal("1.2.0", client.BridgeVersion);
        Assert.Equal(2, client.AcknowledgedProtocolVersion);
        Assert.Equal("multi-screen", client.Capability);
        Assert.False(client.HasRunningSensors);
        var screen = Assert.Single(client.AcknowledgedScreens);
        Assert.Equal("front", screen.screenId);
        Assert.Equal(4096, screen.widthPixels);

        await client.StopAsync();
    }

    [Fact]
    public async Task Client_FragmentedStickyAckAndPointerBatchRoundTripAtomically()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        client.Start(Hello("left", "front"));
        await server.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(server, timeout.Token);

        var batch = Batch(
            Frame("left", 40, Pointer(1, 10f, 20f, 0.1f, 0.2f, 1001)),
            Frame("front", 41, Pointer(7, 2048f, 384f, 0.5f, 0.25f, 1002)));
        await WriteFragmentedEnvelopesAsync(
            server,
            new[] { Ack(), RadarIpcProtocol.Create(RadarIpcMessageType.PointerBatch, 42, batch) },
            timeout.Token);

        var received = await ConsumeBatchAsync(client, timeout.Token);

        Assert.Equal(2, received.screens.Count);
        Assert.Equal("left", received.screens[0].screen.screenId);
        Assert.Equal(40, received.screens[0].sequence);
        Assert.Equal("front", received.screens[1].screen.screenId);
        var pointer = Assert.Single(received.screens[1].pointers);
        Assert.Equal(7, pointer.pointerId);
        Assert.Equal(2048f, pointer.pixelX);
        Assert.Equal(384f, pointer.pixelY);
        Assert.Equal(0.5f, pointer.normalizedX);
        Assert.Equal(0.25f, pointer.normalizedY);
        Assert.Equal(1002, pointer.timestampUnixMilliseconds);

        await client.StopAsync();
    }

    [Fact]
    public async Task Client_TwoUnconsumedBatchesDropOneWholeBatchAndLatestWins()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        await ConnectAndAckAsync(server, client, timeout.Token);

        await WriteFragmentedEnvelopesAsync(
            server,
            new[]
            {
                RadarIpcProtocol.Create(RadarIpcMessageType.PointerBatch, 1, Batch(Frame("main", 1))),
                RadarIpcProtocol.Create(RadarIpcMessageType.PointerBatch, 2, Batch(Frame("main", 2)))
            },
            timeout.Token,
            fragment: false);
        await WaitUntilAsync(() => client.DroppedBatchCount == 1, timeout.Token);

        Assert.True(client.TryConsumeLatestBatch(out var latest));
        Assert.Equal(2, Assert.Single(latest.screens).sequence);
        Assert.Equal(1, client.DroppedBatchCount);
        Assert.Equal(client.DroppedBatchCount, client.DroppedFrameCount);

        await client.StopAsync();
    }

    [Fact]
    public async Task Client_LegacyPointerFrameReportsExplicitErrorAndPublishesNoBatch()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        var laterSubscriberCalled = false;
        client.ErrorReceived += _ => throw new InvalidOperationException("observer failed");
        client.ErrorReceived += _ => laterSubscriberCalled = true;
        await ConnectAndAckAsync(server, client, timeout.Token);

        await WriteEnvelopeAsync(
            server,
            ManualEnvelope(RadarIpcProtocol.Version, RadarIpcMessageType.PointerFrame, new RadarPointerFrameMessage()),
            timeout.Token);
        await WaitUntilAsync(() => client.LastError.Contains("legacy PointerFrame", StringComparison.Ordinal), timeout.Token);
        client.DrainMainThreadEvents();

        Assert.Equal("RadarBridge sent legacy PointerFrame on IPC v2.", client.LastError);
        Assert.True(laterSubscriberCalled);
        Assert.False(client.TryConsumeLatestBatch(out _));
        Assert.True(client.IsConnected);

        await client.StopAsync();
    }

    [Theory]
    [InlineData(1)]
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
            await WriteEnvelopeAsync(
                firstServer,
                ManualEnvelope(protocolVersion, RadarIpcMessageType.PointerBatch, Batch(Frame("main", 1))),
                timeout.Token);
            await WaitUntilAsync(
                () => client.LastError.Contains(protocolVersion.ToString(), StringComparison.Ordinal),
                timeout.Token);
        }

        Assert.False(client.TryConsumeLatestBatch(out _));
        await using var secondServer = Server(pipeName);
        await secondServer.WaitForConnectionAsync(timeout.Token);
        Assert.Equal(RadarIpcMessageType.Hello, (await ReadEnvelopeAsync(secondServer, timeout.Token)).messageType);
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
    public async Task Client_NullBatchPayloadReportsErrorReconnectsAndPublishesNothing()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);

        await using (var firstServer = Server(pipeName))
        {
            await ConnectAndAckAsync(firstServer, client, timeout.Token);
            var invalid = new RadarIpcEnvelope
            {
                protocolVersion = RadarIpcProtocol.Version,
                messageType = RadarIpcMessageType.PointerBatch,
                sequence = 10,
                timestampUnixMilliseconds = 1000,
                payload = JValue.CreateNull()
            };
            await WriteEnvelopeAsync(firstServer, invalid, timeout.Token);
            await WaitUntilAsync(
                () => client.LastError.Contains("PointerBatch", StringComparison.OrdinalIgnoreCase),
                timeout.Token);
        }

        Assert.False(client.TryConsumeLatestBatch(out _));
        await using var secondServer = Server(pipeName);
        await secondServer.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(secondServer, timeout.Token);
        await WriteEnvelopeAsync(secondServer, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);

        await client.StopAsync();
    }

    [Fact]
    public async Task Client_PointerBatchBeforeAckIsRejectedAndSessionReconnects()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);

        await using (var firstServer = Server(pipeName))
        {
            client.Start(Hello("main"));
            await firstServer.WaitForConnectionAsync(timeout.Token);
            await ReadEnvelopeAsync(firstServer, timeout.Token);
            await WriteEnvelopeAsync(
                firstServer,
                RadarIpcProtocol.Create(RadarIpcMessageType.PointerBatch, 2, Batch(Frame("main", 2))),
                timeout.Token);
            await WaitUntilAsync(
                () => client.LastError.Contains("before HelloAck", StringComparison.OrdinalIgnoreCase),
                timeout.Token);
        }

        Assert.False(client.IsConnected);
        Assert.False(client.TryConsumeLatestBatch(out _));
        await using var secondServer = Server(pipeName);
        await secondServer.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(secondServer, timeout.Token);
        await WriteEnvelopeAsync(secondServer, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);

        await client.StopAsync();
    }

    [Fact]
    public async Task Client_InvalidAckProtocolReportsErrorAndDoesNotConnect()
    {
        var pipeName = PipeName();
        using var timeout = Timeout();
        using var client = Client(pipeName);

        await using (var firstServer = Server(pipeName))
        {
            client.Start(Hello("main"));
            await firstServer.WaitForConnectionAsync(timeout.Token);
            await ReadEnvelopeAsync(firstServer, timeout.Token);
            var ack = Ack();
            ack.payload["protocolVersion"] = 1;

            await WriteEnvelopeAsync(firstServer, ack, timeout.Token);
            await WaitUntilAsync(
                () => client.LastError.Contains("acknowledgement", StringComparison.OrdinalIgnoreCase),
                timeout.Token);
        }

        Assert.False(client.IsConnected);

        await using var secondServer = Server(pipeName);
        await secondServer.WaitForConnectionAsync(timeout.Token);
        await ReadEnvelopeAsync(secondServer, timeout.Token);
        await WriteEnvelopeAsync(secondServer, Ack(), timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        await client.StopAsync();
    }

    [Fact]
    public async Task Client_HeartbeatAndShutdownRemainLengthPrefixedMessages()
    {
        var pipeName = PipeName();
        using var timeout = Timeout(seconds: 7);
        await using var server = Server(pipeName);
        using var client = Client(pipeName);
        await ConnectAndAckAsync(server, client, timeout.Token);

        var heartbeat = await ReadEnvelopeAsync(server, timeout.Token);
        Assert.Equal(RadarIpcMessageType.Ping, heartbeat.messageType);
        var sendShutdown = client.SendShutdownAsync();
        var shutdown = await ReadEnvelopeAsync(server, timeout.Token);
        await sendShutdown;
        Assert.Equal(RadarIpcMessageType.Shutdown, shutdown.messageType);

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
        Assert.False(client.TryConsumeLatestBatch(out _));
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

    private static RadarPipeClient Client(string pipeName) => new(pipeName, 500, 50, 2500);

    private static NamedPipeServerStream Server(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private static CancellationTokenSource Timeout(int seconds = 5) =>
        new(TimeSpan.FromSeconds(seconds));

    private static string PipeName() => "RadarControl.Tests." + Guid.NewGuid().ToString("N");

    private static RadarHelloPayload Hello(params string[] screenIds)
    {
        return new RadarHelloPayload
        {
            unityProcessId = Environment.ProcessId,
            unityVersion = "2021.3.45f1",
            screens = screenIds.Select((id, index) => new RadarScreenDefinitionPayload
            {
                screenId = id,
                name = id.ToUpperInvariant(),
                defaultWidthPixels = id == "front" ? 4096 : 1920,
                defaultHeightPixels = id == "front" ? 1536 : 1080,
                isPrimary = id == "front" || (screenIds.Length == 1 && index == 0),
                order = index
            }).ToList()
        };
    }

    private static RadarIpcEnvelope Ack(bool connected = true)
    {
        return RadarIpcProtocol.Create(
            RadarIpcMessageType.HelloAck,
            1,
            new RadarHelloAckPayload
            {
                bridgeVersion = "1.2.0",
                protocolVersion = 2,
                connected = connected,
                capability = "multi-screen",
                screens = new List<RadarScreenInfo>
                {
                    new()
                    {
                        screenId = "front",
                        name = "Front",
                        widthPixels = 4096,
                        heightPixels = 1536,
                        isPrimary = true,
                        order = 0
                    }
                }
            });
    }

    private static RadarPointerBatchPayload Batch(params RadarScreenPointerFrame[] frames) => new()
    {
        screens = frames.ToList()
    };

    private static RadarScreenPointerFrame Frame(
        string screenId,
        long sequence,
        params RadarScreenPointer[] pointers)
    {
        return new RadarScreenPointerFrame
        {
            screen = new RadarScreenInfo
            {
                screenId = screenId,
                name = screenId,
                widthPixels = 1920,
                heightPixels = 1080,
                isPrimary = screenId == "main" || screenId == "front",
                order = 0
            },
            sequence = sequence,
            timestampUnixMilliseconds = 1000 + sequence,
            pointers = pointers.ToList()
        };
    }

    private static RadarScreenPointer Pointer(
        int pointerId,
        float pixelX,
        float pixelY,
        float normalizedX,
        float normalizedY,
        long timestamp,
        RadarPointerPhase phase = RadarPointerPhase.Move)
    {
        return new RadarScreenPointer
        {
            pointerId = pointerId,
            phase = phase,
            normalizedX = normalizedX,
            normalizedY = normalizedY,
            pixelX = pixelX,
            pixelY = pixelY,
            confidence = 0.9f,
            timestampUnixMilliseconds = timestamp
        };
    }

    private static RadarIpcEnvelope ManualEnvelope(int protocol, RadarIpcMessageType type, object payload)
    {
        return new RadarIpcEnvelope
        {
            protocolVersion = protocol,
            messageType = type,
            sequence = 9,
            timestampUnixMilliseconds = 1000,
            payload = payload == null ? JValue.CreateNull() : JToken.FromObject(payload)
        };
    }

    private static async Task ConnectAndAckAsync(
        NamedPipeServerStream server,
        RadarPipeClient client,
        CancellationToken cancellationToken)
    {
        client.Start(Hello("main"));
        await server.WaitForConnectionAsync(cancellationToken);
        await ReadEnvelopeAsync(server, cancellationToken);
        await WriteEnvelopeAsync(server, Ack(), cancellationToken);
        await WaitUntilAsync(() => client.IsConnected, cancellationToken);
    }

    private static async Task<RadarPointerBatchPayload> ConsumeBatchAsync(
        RadarPipeClient client,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (client.TryConsumeLatestBatch(out var batch))
            {
                return batch;
            }

            await Task.Delay(5, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task<RadarIpcEnvelope> ReadEnvelopeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        return (await ReadEnvelopeWithJsonAsync(stream, cancellationToken)).Envelope;
    }

    private static async Task<(RadarIpcEnvelope Envelope, string Json)> ReadEnvelopeWithJsonAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        var json = Encoding.UTF8.GetString(payload);
        return (JsonConvert.DeserializeObject<RadarIpcEnvelope>(json)!, json);
    }

    private static async Task WriteEnvelopeAsync(
        Stream stream,
        RadarIpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await WriteRawJsonAsync(stream, JsonConvert.SerializeObject(envelope), cancellationToken);
    }

    private static async Task WriteRawJsonAsync(
        Stream stream,
        string json,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteFragmentedEnvelopesAsync(
        Stream stream,
        IReadOnlyList<RadarIpcEnvelope> envelopes,
        CancellationToken cancellationToken,
        bool fragment = true)
    {
        var frames = envelopes.Select(EncodeEnvelope).ToArray();
        var combined = new byte[frames.Sum(bytes => bytes.Length)];
        var offset = 0;
        foreach (var frame in frames)
        {
            Buffer.BlockCopy(frame, 0, combined, offset, frame.Length);
            offset += frame.Length;
        }

        if (!fragment)
        {
            await stream.WriteAsync(combined, cancellationToken);
        }
        else
        {
            var first = Math.Min(3, combined.Length);
            var second = Math.Min(11, combined.Length - first);
            await stream.WriteAsync(combined.AsMemory(0, first), cancellationToken);
            await stream.WriteAsync(combined.AsMemory(first, second), cancellationToken);
            await stream.WriteAsync(combined.AsMemory(first + second), cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }

    private static byte[] EncodeEnvelope(RadarIpcEnvelope envelope)
    {
        var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(envelope));
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(5, cancellationToken);
        }
    }
}
