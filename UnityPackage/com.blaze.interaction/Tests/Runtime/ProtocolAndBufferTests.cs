using System;
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
