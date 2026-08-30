#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json;

namespace Blaze.Interaction
{
    [Serializable]
    public sealed class HandLandmarkExtension
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("normalizedPosition")]
        public Vector2Data NormalizedPosition { get; set; }

        [JsonProperty("pixelPosition")]
        public Vector2Data PixelPosition { get; set; }

        [JsonProperty("z")]
        public float Z { get; set; }

        internal bool IsValid(int expectedIndex)
        {
            return Index == expectedIndex &&
                   NormalizedPosition != null &&
                   PixelPosition != null &&
                   !float.IsNaN(Z) &&
                   !float.IsInfinity(Z);
        }
    }

    [Serializable]
    public sealed class HandInteractionExtension
    {
        public const int CurrentSchemaVersion = 1;
        public const int LandmarkCount = 21;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("trackingPoint")]
        public string TrackingPoint { get; set; }

        [JsonProperty("landmarks")]
        public List<HandLandmarkExtension> Landmarks { get; set; } =
            new List<HandLandmarkExtension>();

        internal bool IsValid()
        {
            if (SchemaVersion != CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(TrackingPoint) ||
                Landmarks == null ||
                Landmarks.Count != LandmarkCount)
            {
                return false;
            }

            for (var index = 0; index < Landmarks.Count; index++)
            {
                var landmark = Landmarks[index];
                if (landmark == null || !landmark.IsValid(index))
                {
                    return false;
                }
            }

            Landmarks = new List<HandLandmarkExtension>(Landmarks);
            return true;
        }
    }

    public readonly struct HandSkeletonConnection
    {
        public HandSkeletonConnection(int startIndex, int endIndex)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
        }

        public int StartIndex { get; }
        public int EndIndex { get; }
    }

    public static class HandSkeletonConnections
    {
        private static readonly ReadOnlyCollection<HandSkeletonConnection> Connections =
            Array.AsReadOnly(new[]
            {
                new HandSkeletonConnection(0, 1),
                new HandSkeletonConnection(1, 2),
                new HandSkeletonConnection(2, 3),
                new HandSkeletonConnection(3, 4),
                new HandSkeletonConnection(0, 5),
                new HandSkeletonConnection(5, 6),
                new HandSkeletonConnection(6, 7),
                new HandSkeletonConnection(7, 8),
                new HandSkeletonConnection(5, 9),
                new HandSkeletonConnection(9, 10),
                new HandSkeletonConnection(10, 11),
                new HandSkeletonConnection(11, 12),
                new HandSkeletonConnection(9, 13),
                new HandSkeletonConnection(13, 14),
                new HandSkeletonConnection(14, 15),
                new HandSkeletonConnection(15, 16),
                new HandSkeletonConnection(13, 17),
                new HandSkeletonConnection(17, 18),
                new HandSkeletonConnection(18, 19),
                new HandSkeletonConnection(19, 20),
                new HandSkeletonConnection(0, 17)
            });

        public static IReadOnlyList<HandSkeletonConnection> All
        {
            get { return Connections; }
        }
    }
}
