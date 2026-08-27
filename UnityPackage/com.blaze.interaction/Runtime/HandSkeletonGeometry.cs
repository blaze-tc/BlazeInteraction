#nullable disable

using System;
using System.Collections.Generic;

namespace Blaze.Interaction
{
    public readonly struct HandSkeletonKey : IEquatable<HandSkeletonKey>
    {
        public HandSkeletonKey(string providerInstanceId, string surfaceId, long pointId)
        {
            ProviderInstanceId = providerInstanceId ?? string.Empty;
            SurfaceId = surfaceId ?? string.Empty;
            PointId = pointId;
        }

        public string ProviderInstanceId { get; }
        public string SurfaceId { get; }
        public long PointId { get; }

        public bool Equals(HandSkeletonKey other)
        {
            return PointId == other.PointId &&
                   string.Equals(ProviderInstanceId, other.ProviderInstanceId, StringComparison.Ordinal) &&
                   string.Equals(SurfaceId, other.SurfaceId, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is HandSkeletonKey && Equals((HandSkeletonKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(ProviderInstanceId);
                hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(SurfaceId);
                return hash * 397 ^ PointId.GetHashCode();
            }
        }

        public static bool operator ==(HandSkeletonKey left, HandSkeletonKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(HandSkeletonKey left, HandSkeletonKey right)
        {
            return !left.Equals(right);
        }
    }

    public sealed class HandSkeletonJointGeometry
    {
        internal HandSkeletonJointGeometry(int index, Vector2Data normalizedPosition)
        {
            Index = index;
            NormalizedPosition = normalizedPosition;
        }

        public int Index { get; }
        public Vector2Data NormalizedPosition { get; }
    }

    public sealed class HandSkeletonBoneGeometry
    {
        internal HandSkeletonBoneGeometry(
            int startIndex,
            int endIndex,
            Vector2Data startPosition,
            Vector2Data endPosition)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
            StartPosition = startPosition;
            EndPosition = endPosition;
        }

        public int StartIndex { get; }
        public int EndIndex { get; }
        public Vector2Data StartPosition { get; }
        public Vector2Data EndPosition { get; }
    }

    public sealed class HandSkeletonTrackGeometry
    {
        internal HandSkeletonTrackGeometry(
            HandSkeletonKey key,
            HandSkeletonJointGeometry[] joints,
            HandSkeletonBoneGeometry[] bones)
        {
            Key = key;
            Joints = Array.AsReadOnly(joints);
            Bones = Array.AsReadOnly(bones);
        }

        public HandSkeletonKey Key { get; }
        public IReadOnlyList<HandSkeletonJointGeometry> Joints { get; }
        public IReadOnlyList<HandSkeletonBoneGeometry> Bones { get; }
    }

    public sealed class HandSkeletonGeometry
    {
        public const float PixelConsistencyTolerance = 1f;

        private readonly Dictionary<HandSkeletonKey, HandSkeletonTrackGeometry> tracks =
            new Dictionary<HandSkeletonKey, HandSkeletonTrackGeometry>();

        public int Count { get { return tracks.Count; } }

        public bool Contains(HandSkeletonKey key)
        {
            return tracks.ContainsKey(key);
        }

        public bool TryApply(
            InteractionPoint point,
            InteractionSurface surface,
            out HandSkeletonTrackGeometry track)
        {
            track = null;
            if (point == null)
            {
                return false;
            }

            var key = new HandSkeletonKey(point.ProviderInstanceId, point.SurfaceId, point.Id);
            if (point.Phase == InteractionPhase.Up || point.Phase == InteractionPhase.Cancel)
            {
                tracks.Remove(key);
                return false;
            }

            HandInteractionExtension hand;
            if (!IsValidSurface(point, surface) || !point.TryGetHandExtension(out hand))
            {
                tracks.Remove(key);
                return false;
            }

            var joints = new HandSkeletonJointGeometry[HandInteractionExtension.LandmarkCount];
            for (var index = 0; index < hand.Landmarks.Count; index++)
            {
                var landmark = hand.Landmarks[index];
                if (!IsConsistent(landmark, surface))
                {
                    tracks.Remove(key);
                    return false;
                }

                joints[index] = new HandSkeletonJointGeometry(
                    index,
                    new Vector2Data
                    {
                        X = landmark.NormalizedPosition.X,
                        Y = landmark.NormalizedPosition.Y
                    });
            }

            var bones = new HandSkeletonBoneGeometry[HandSkeletonConnections.All.Count];
            for (var index = 0; index < HandSkeletonConnections.All.Count; index++)
            {
                var connection = HandSkeletonConnections.All[index];
                bones[index] = new HandSkeletonBoneGeometry(
                    connection.StartIndex,
                    connection.EndIndex,
                    joints[connection.StartIndex].NormalizedPosition,
                    joints[connection.EndIndex].NormalizedPosition);
            }

            track = new HandSkeletonTrackGeometry(key, joints, bones);
            tracks[key] = track;
            return true;
        }

        public bool Remove(InteractionPoint point)
        {
            if (point == null)
            {
                return false;
            }

            return tracks.Remove(new HandSkeletonKey(
                point.ProviderInstanceId,
                point.SurfaceId,
                point.Id));
        }

        public void Clear()
        {
            tracks.Clear();
        }

        private static bool IsValidSurface(InteractionPoint point, InteractionSurface surface)
        {
            return surface != null &&
                   surface.LogicalWidth > 0 &&
                   surface.LogicalHeight > 0 &&
                   string.Equals(point.SurfaceId, surface.SurfaceId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsConsistent(
            HandLandmarkExtension landmark,
            InteractionSurface surface)
        {
            var normalized = landmark.NormalizedPosition;
            var pixel = landmark.PixelPosition;
            if (!IsFinite(normalized.X) || !IsFinite(normalized.Y) ||
                normalized.X < 0f || normalized.X > 1f ||
                normalized.Y < 0f || normalized.Y > 1f ||
                !IsFinite(pixel.X) || !IsFinite(pixel.Y))
            {
                return false;
            }

            var expectedX = normalized.X * surface.LogicalWidth;
            var expectedY = normalized.Y * surface.LogicalHeight;
            return Math.Abs(expectedX - pixel.X) <= PixelConsistencyTolerance &&
                   Math.Abs(expectedY - pixel.Y) <= PixelConsistencyTolerance;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
