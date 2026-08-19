using System;
using UnityEngine;

namespace Blaze.Radar
{
    [Serializable]
    public sealed class RadarPointerState
    {
        public string ScreenId;
        public int PointerId;
        public Vector2 NormalizedPosition;
        public Vector2 LogicalPixelPosition;
        public Vector2 ScreenPosition;
        public RadarPointerPhase Phase;
        public float Confidence;
        public long TimestampUnixMilliseconds;

        public void Apply(RadarPointerMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            ScreenId = string.Empty;
            PointerId = message.pointerId;
            NormalizedPosition = new Vector2(message.normalizedX, message.normalizedY);
            ScreenPosition = new Vector2(message.normalizedX * Screen.width, message.normalizedY * Screen.height);
            LogicalPixelPosition = ScreenPosition;
            Phase = message.phase;
            Confidence = message.confidence;
            TimestampUnixMilliseconds = message.timestampUnixMilliseconds;
        }

        public void Apply(RadarScreenInfo screen, RadarScreenPointer pointer)
        {
            if (screen == null)
            {
                throw new ArgumentNullException(nameof(screen));
            }

            if (pointer == null)
            {
                throw new ArgumentNullException(nameof(pointer));
            }

            ScreenId = screen.screenId ?? string.Empty;
            PointerId = pointer.pointerId;
            NormalizedPosition = new Vector2(pointer.normalizedX, pointer.normalizedY);
            LogicalPixelPosition = new Vector2(pointer.pixelX, pointer.pixelY);
            ScreenPosition = LogicalPixelPosition;
            Phase = pointer.phase;
            Confidence = pointer.confidence;
            TimestampUnixMilliseconds = pointer.timestampUnixMilliseconds;
        }
    }
}
