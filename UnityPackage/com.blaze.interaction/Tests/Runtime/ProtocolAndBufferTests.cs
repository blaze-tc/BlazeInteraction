using System;
using System.Collections.Generic;
using System.Text;
using Blaze.Interaction.Internal;
using NUnit.Framework;

namespace Blaze.Interaction.Tests
{
    public sealed class ProtocolAndBufferTests
    {
        [Test]
        public void ProtocolOneHello_UsesProviderNeutralPipeAndCallerTopology()
        {
            var hello = new HelloPayload
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

            var envelope = InteractionIpcProtocol.Create(InteractionMessageType.Hello, 7, hello);
            var json = InteractionIpcProtocol.Serialize(envelope);

            Assert.That(InteractionIpcProtocol.Version, Is.EqualTo(1));
            Assert.That(InteractionIpcProtocol.PipeName, Is.EqualTo("Blaze.InteractionBridge"));
            Assert.That(envelope.ProtocolVersion, Is.EqualTo(1));
            Assert.That(envelope.MessageType, Is.EqualTo(InteractionMessageType.Hello));
            Assert.That(json, Does.Contain("\"messageType\":\"Hello\""));
            Assert.That(json, Does.Contain("\"surfaceId\":\"FRONT\""));
            Assert.That(json, Does.Not.Contain("radar"));
        }

        [Test]
        public void Protocol_DeserializesFootprintAndDefaultsMissingFootprintToEmpty()
        {
            const string pointJson = "\"id\":7,\"surfaceId\":\"front\",\"providerId\":\"p\",\"providerInstanceId\":\"i\",\"sourceId\":\"s\",\"phase\":\"Move\",\"normalizedPosition\":{\"x\":0.5,\"y\":0.5},\"pixelPosition\":{\"x\":100,\"y\":200},\"confidence\":1,\"timestampUnixMs\":2,\"extensions\":null";
            const string json = "{\"messageType\":\"InteractionFrame\",\"protocolVersion\":1,\"sequence\":1,\"payload\":{\"providerId\":\"p\",\"providerInstanceId\":\"i\",\"surfaceId\":\"front\",\"sequence\":1,\"timestampUnixMs\":2,\"points\":[{" + pointJson + ",\"fp\":[{\"x\":11,\"y\":22},{\"x\":33,\"y\":44}]}]}}";

            var point = InteractionIpcProtocol.Deserialize(json)
                .DeserializePayload<InteractionFrame>().Points[0];

            Assert.That(point.Fp, Has.Count.EqualTo(2));
            Assert.That(point.Fp[1].X, Is.EqualTo(33f));

            const string missingFootprintJson = "{\"messageType\":\"InteractionFrame\",\"protocolVersion\":1,\"sequence\":1,\"payload\":{\"providerId\":\"p\",\"providerInstanceId\":\"i\",\"surfaceId\":\"front\",\"sequence\":1,\"timestampUnixMs\":2,\"points\":[{" + pointJson + "}]}}";
            var missingFootprintPoint = InteractionIpcProtocol.Deserialize(missingFootprintJson)
                .DeserializePayload<InteractionFrame>().Points[0];

            Assert.That(missingFootprintPoint.Fp, Is.Not.Null);
            Assert.That(missingFootprintPoint.Fp, Is.Empty);
        }

        [Test]
        public void CameraFrame_RoundTripsCenterAndTwentyOneLandmarkFootprint()
        {
            var footprint = new List<Vector2Data>();
            for (var index = 0; index < 21; index++)
            {
                footprint.Add(new Vector2Data { X = index * 10f, Y = index * 5f });
            }

            var frame = new InteractionFrame
            {
                ProviderId = "blaze.camera.vision",
                ProviderInstanceId = "camera-main",
                SurfaceId = "main",
                Sequence = 7,
                TimestampUnixMs = 1234,
                Points =
                {
                    new InteractionPoint
                    {
                        Id = 1,
                        SurfaceId = "main",
                        ProviderId = "blaze.camera.vision",
                        ProviderInstanceId = "camera-main",
                        SourceId = "hand-track-1",
                        Phase = InteractionPhase.Hover,
                        NormalizedPosition = new Vector2Data { X = 0.25f, Y = 0.75f },
                        PixelPosition = new Vector2Data { X = 480f, Y = 270f },
                        Confidence = 1f,
                        TimestampUnixMs = 1234,
                        Fp = footprint
                    }
                }
            };

            var json = InteractionIpcProtocol.Serialize(InteractionIpcProtocol.Create(
                InteractionMessageType.InteractionFrame,
                frame.Sequence,
                frame));
            var point = InteractionIpcProtocol.Deserialize(json)
                .DeserializePayload<InteractionFrame>().Points[0];

            Assert.That(point.PixelPosition.X, Is.EqualTo(480f));
            Assert.That(point.PixelPosition.Y, Is.EqualTo(270f));
            Assert.That(point.Fp, Has.Count.EqualTo(21));
            Assert.That(point.Fp[20].X, Is.EqualTo(200f));
            Assert.That(point.Fp[20].Y, Is.EqualTo(100f));
        }

        [Test]
        public void FragmentedAndStickyFrames_AreDecodedWithoutMessageBoundaryLoss()
        {
            var first = Frame("one");
            var second = Frame("two");
            var decoder = new LengthPrefixedFrameDecoder();

            Assert.That(decoder.Append(first, 0, 2), Is.Empty);
            var remainder = new byte[first.Length - 2 + second.Length];
            Buffer.BlockCopy(first, 2, remainder, 0, first.Length - 2);
            Buffer.BlockCopy(second, 0, remainder, first.Length - 2, second.Length);
            var frames = decoder.Append(remainder, 0, remainder.Length);

            Assert.That(frames, Has.Count.EqualTo(2));
            Assert.That(Encoding.UTF8.GetString(frames[0]), Is.EqualTo("one"));
            Assert.That(Encoding.UTF8.GetString(frames[1]), Is.EqualTo("two"));
        }

        [Test]
        public void LatestFrameBuffer_RemainsSingleSlotAndReportsOverwrites()
        {
            var buffer = new LatestValueBuffer<int>();

            buffer.Publish(1);
            buffer.Publish(2);
            buffer.Publish(3);

            Assert.That(buffer.PendingCount, Is.EqualTo(1));
            Assert.That(buffer.DroppedCount, Is.EqualTo(2));
            Assert.That(buffer.TryConsume(out var value), Is.True);
            Assert.That(value, Is.EqualTo(3));
            Assert.That(buffer.TryConsume(out _), Is.False);
        }

        private static byte[] Frame(string value)
        {
            var payload = Encoding.UTF8.GetBytes(value);
            var frame = new byte[payload.Length + 4];
            frame[0] = (byte)payload.Length;
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)(payload.Length >> 16);
            frame[3] = (byte)(payload.Length >> 24);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            return frame;
        }
    }
}
