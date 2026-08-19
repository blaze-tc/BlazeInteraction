using System;
using System.Collections.Generic;
using System.IO;

namespace Blaze.Radar.Internal
{
    public sealed class LengthPrefixedFrameDecoder
    {
        private readonly List<byte> _buffer = new List<byte>();
        private readonly int _maximumPayloadLength;

        public LengthPrefixedFrameDecoder(int maximumPayloadLength = 4 * 1024 * 1024)
        {
            if (maximumPayloadLength < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPayloadLength));
            }

            _maximumPayloadLength = maximumPayloadLength;
        }

        public int BufferedByteCount => _buffer.Count;

        public IReadOnlyList<byte[]> Append(byte[] bytes, int offset, int count)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (offset < 0 || count < 0 || offset + count > bytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            for (var index = offset; index < offset + count; index++)
            {
                _buffer.Add(bytes[index]);
            }

            var frames = new List<byte[]>();
            while (_buffer.Count >= 4)
            {
                var length = _buffer[0]
                    | _buffer[1] << 8
                    | _buffer[2] << 16
                    | _buffer[3] << 24;
                if (length <= 0 || length > _maximumPayloadLength)
                {
                    _buffer.Clear();
                    throw new InvalidDataException($"IPC payload length {length} is outside the allowed range.");
                }

                if (_buffer.Count < length + 4)
                {
                    break;
                }

                var payload = _buffer.GetRange(4, length).ToArray();
                _buffer.RemoveRange(0, length + 4);
                frames.Add(payload);
            }

            return frames;
        }

        public void Reset()
        {
            _buffer.Clear();
        }
    }
}
