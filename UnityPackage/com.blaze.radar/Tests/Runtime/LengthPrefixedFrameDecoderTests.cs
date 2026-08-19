using System;
using System.Text;
using NUnit.Framework;
using Blaze.Radar.Internal;

namespace Blaze.Radar.Tests
{
    public sealed class LengthPrefixedFrameDecoderTests
    {
        [Test]
        public void HalfAndStickyFrames_AreDecodedWithoutNewlineBoundaries()
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
