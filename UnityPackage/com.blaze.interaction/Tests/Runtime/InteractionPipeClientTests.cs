using System;
using System.Collections;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Blaze.Interaction.Tests
{
    public sealed class InteractionPipeClientTests
    {
        [UnityTest]
        public IEnumerator Client_HelloAckGatesConnectionAndLatestFrameIsBounded()
        {
            var task = VerifyHandshakeAndLatestFrameAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                throw task.Exception.InnerException;
            }
        }

        [UnityTest]
        public IEnumerator Client_ReconnectsWithFreshHandshakeAndClearsPreviousSessionState()
        {
            var task = VerifyReconnectResetsSessionAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                throw task.Exception.InnerException;
            }
        }

        private static async Task VerifyHandshakeAndLatestFrameAsync()
        {
            var pipeName = "Blaze.Interaction.Tests." + Guid.NewGuid().ToString("N");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var server = new NamedPipeServerStream(
                       pipeName,
                       PipeDirection.InOut,
                       1,
                       PipeTransmissionMode.Byte,
                       PipeOptions.Asynchronous))
            using (var client = new InteractionPipeClient(pipeName, 500, 25, 2000))
            {
                client.Start(Hello());
                await server.WaitForConnectionAsync(timeout.Token);
                var hello = await ReadEnvelopeAsync(server, timeout.Token);

                Assert.That(hello.ProtocolVersion, Is.EqualTo(1));
                Assert.That(hello.MessageType, Is.EqualTo(InteractionMessageType.Hello));
                Assert.That(client.IsConnected, Is.False);

                await WriteEnvelopeAsync(
                    server,
                    InteractionIpcProtocol.Create(
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
                            Capabilities = { "interaction-point", "multi-surface" }
                        }),
                    timeout.Token);
                await WaitUntilAsync(() => client.IsConnected, timeout.Token);

                for (var sequence = 1; sequence <= 3; sequence++)
                {
                    await WriteEnvelopeAsync(
                        server,
                        InteractionIpcProtocol.Create(
                            InteractionMessageType.InteractionFrame,
                            sequence + 1,
                            Frame(sequence)),
                        timeout.Token);
                }

                await WaitUntilAsync(() => client.DroppedFrameCount == 2, timeout.Token);
                Assert.That(client.TryConsumeLatestFrame(out var latest), Is.True);
                Assert.That(latest.Sequence, Is.EqualTo(3));
                Assert.That(client.TryConsumeLatestFrame(out _), Is.False);
                await client.StopAsync();
            }
        }

        private static async Task VerifyReconnectResetsSessionAsync()
        {
            var pipeName = "Blaze.Interaction.Tests." + Guid.NewGuid().ToString("N");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (var client = new InteractionPipeClient(pipeName, 500, 25, 2000))
            {
                client.Start(Hello());

                using (var firstServer = CreateServer(pipeName))
                {
                    await firstServer.WaitForConnectionAsync(timeout.Token);
                    var firstHello = await ReadEnvelopeAsync(firstServer, timeout.Token);
                    Assert.That(firstHello.MessageType, Is.EqualTo(InteractionMessageType.Hello));

                    await WriteEnvelopeAsync(
                        firstServer,
                        HelloAck("radar-first"),
                        timeout.Token);
                    await WaitUntilAsync(
                        () => client.IsConnected && client.ActiveProvider != null,
                        timeout.Token);
                    Assert.That(client.ActiveProvider.InstanceId, Is.EqualTo("radar-first"));

                    await WriteEnvelopeAsync(
                        firstServer,
                        InteractionIpcProtocol.Create(
                            InteractionMessageType.InteractionFrame,
                            2,
                            Frame(1)),
                        timeout.Token);
                    InteractionFrame firstFrame = null;
                    await WaitUntilAsync(
                        () => client.TryConsumeLatestFrame(out firstFrame),
                        timeout.Token);
                    Assert.That(firstFrame.Sequence, Is.EqualTo(1));

                    await WriteEnvelopeAsync(
                        firstServer,
                        InteractionIpcProtocol.Create(
                            InteractionMessageType.InteractionFrame,
                            3,
                            Frame(2)),
                        timeout.Token);
                    await WriteEnvelopeAsync(
                        firstServer,
                        InteractionIpcProtocol.Create(
                            InteractionMessageType.InteractionFrame,
                            4,
                            Frame(3)),
                        timeout.Token);
                    await WaitUntilAsync(() => client.DroppedFrameCount >= 1, timeout.Token);
                }

                await WaitUntilAsync(
                    () => !client.IsConnected && client.ActiveProvider == null,
                    timeout.Token);
                Assert.That(client.TryConsumeLatestFrame(out _), Is.False);

                using (var secondServer = CreateServer(pipeName))
                {
                    await secondServer.WaitForConnectionAsync(timeout.Token);
                    var secondHello = await ReadEnvelopeAsync(secondServer, timeout.Token);
                    Assert.That(secondHello.MessageType, Is.EqualTo(InteractionMessageType.Hello));

                    await WriteEnvelopeAsync(
                        secondServer,
                        HelloAck("radar-second"),
                        timeout.Token);
                    await WaitUntilAsync(
                        () => client.IsConnected &&
                              client.ActiveProvider != null &&
                              client.ActiveProvider.InstanceId == "radar-second",
                        timeout.Token);
                    Assert.That(client.TryConsumeLatestFrame(out _), Is.False);

                    await WriteEnvelopeAsync(
                        secondServer,
                        InteractionIpcProtocol.Create(
                            InteractionMessageType.InteractionFrame,
                            6,
                            Frame(100)),
                        timeout.Token);
                    InteractionFrame secondFrame = null;
                    await WaitUntilAsync(
                        () => client.TryConsumeLatestFrame(out secondFrame),
                        timeout.Token);
                    Assert.That(secondFrame.Sequence, Is.EqualTo(100));
                }

                await client.StopAsync();
            }
        }

        private static NamedPipeServerStream CreateServer(string pipeName)
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        private static InteractionEnvelope HelloAck(string providerInstanceId)
        {
            return InteractionIpcProtocol.Create(
                InteractionMessageType.HelloAck,
                1,
                new HelloAckPayload
                {
                    BridgeVersion = "1.0.0",
                    ActiveProvider = new ProviderReferencePayload
                    {
                        Id = "blaze.radar.f10f20",
                        InstanceId = providerInstanceId
                    },
                    Capabilities = { "interaction-point", "multi-surface" }
                });
        }

        private static HelloPayload Hello()
        {
            return new HelloPayload
            {
                UnityPid = 42,
                UnityVersion = "2021.3.45f1",
                SdkVersion = "1.0.0",
                Surfaces =
                {
                    new InteractionSurface
                    {
                        SurfaceId = "FRONT",
                        Name = "Front",
                        LogicalWidth = 1920,
                        LogicalHeight = 1080,
                        IsPrimary = true,
                        Order = 0
                    }
                }
            };
        }

        private static InteractionFrame Frame(long sequence)
        {
            return new InteractionFrame
            {
                ProviderId = "blaze.radar.f10f20",
                ProviderInstanceId = "radar-main",
                SurfaceId = "FRONT",
                Sequence = sequence,
                TimestampUnixMs = 1000 + sequence
            };
        }

        private static async Task<InteractionEnvelope> ReadEnvelopeAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            var prefix = await ReadExactlyAsync(stream, 4, cancellationToken);
            var length = prefix[0] | prefix[1] << 8 | prefix[2] << 16 | prefix[3] << 24;
            var payload = await ReadExactlyAsync(stream, length, cancellationToken);
            return InteractionIpcProtocol.Deserialize(Encoding.UTF8.GetString(payload));
        }

        private static async Task WriteEnvelopeAsync(
            Stream stream,
            InteractionEnvelope envelope,
            CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(InteractionIpcProtocol.Serialize(envelope));
            var frame = new byte[payload.Length + 4];
            frame[0] = (byte)payload.Length;
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)(payload.Length >> 16);
            frame[3] = (byte)(payload.Length >> 24);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            await stream.WriteAsync(frame, 0, frame.Length, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static async Task<byte[]> ReadExactlyAsync(
            Stream stream,
            int length,
            CancellationToken cancellationToken)
        {
            var result = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var count = await stream.ReadAsync(result, offset, length - offset, cancellationToken);
                if (count == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += count;
            }

            return result;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            while (!condition())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(10, cancellationToken);
            }
        }
    }
}
