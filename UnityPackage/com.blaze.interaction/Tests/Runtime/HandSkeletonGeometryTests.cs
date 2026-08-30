#nullable disable

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Blaze.Interaction.Tests
{
    public sealed class HandSkeletonGeometryTests
    {
        [Test]
        public void ValidHand_GeneratesTwentyOneJointsAndEveryFixedConnection()
        {
            var geometry = new HandSkeletonGeometry();
            var point = HandInteractionExtensionTests.PointWithHandExtension(
                HandInteractionExtensionTests.CreateHandExtension());

            HandSkeletonTrackGeometry track;
            Assert.That(geometry.TryApply(point, Surface(), out track), Is.True);
            Assert.That(track, Is.Not.Null);
            Assert.That(track.Joints, Has.Count.EqualTo(21));
            Assert.That(track.Bones, Has.Count.EqualTo(HandSkeletonConnections.All.Count));
            Assert.That(track.Joints.Select(joint => joint.Index),
                Is.EqualTo(Enumerable.Range(0, 21)));
            Assert.That(track.Bones.Select(bone => bone.StartIndex),
                Is.EqualTo(HandSkeletonConnections.All.Select(connection => connection.StartIndex)));
            Assert.That(track.Bones.Select(bone => bone.EndIndex),
                Is.EqualTo(HandSkeletonConnections.All.Select(connection => connection.EndIndex)));
            Assert.That(track.Joints[8].NormalizedPosition.X, Is.EqualTo(.4f));
            Assert.That(track.Joints[8].NormalizedPosition.Y, Is.EqualTo(.4f));
        }

        [Test]
        public void PixelAndNormalizedInconsistency_IsRejectedWithoutCreatingATrack()
        {
            var geometry = new HandSkeletonGeometry();
            var mismatchedPixel = HandInteractionExtensionTests.PointWithHandExtension(
                HandInteractionExtensionTests.CreateHandExtension(seed: 25));
            var outOfRangeNormalized = HandInteractionExtensionTests.PointWithHandExtension(
                HandInteractionExtensionTests.CreateHandExtension(normalizedXOffset: 2f));

            HandSkeletonTrackGeometry ignored;
            Assert.That(geometry.TryApply(mismatchedPixel, Surface(), out ignored), Is.False);
            Assert.That(geometry.TryApply(outOfRangeNormalized, Surface(), out ignored), Is.False);
            Assert.That(geometry.Count, Is.Zero);
        }

        [Test]
        public void SeparateTrackIds_CreateIndependentGeometryAndCancelRemovesOnlyOne()
        {
            var geometry = new HandSkeletonGeometry();
            var first = HandInteractionExtensionTests.PointWithHandExtension(
                HandInteractionExtensionTests.CreateHandExtension());
            first.Id = 7;
            var second = HandInteractionExtensionTests.PointWithHandExtension(
                HandInteractionExtensionTests.CreateHandExtension());
            second.Id = 8;

            HandSkeletonTrackGeometry firstTrack;
            HandSkeletonTrackGeometry secondTrack;
            Assert.That(geometry.TryApply(first, Surface(), out firstTrack), Is.True);
            Assert.That(geometry.TryApply(second, Surface(), out secondTrack), Is.True);
            Assert.That(geometry.Count, Is.EqualTo(2));
            Assert.That(firstTrack.Key, Is.Not.EqualTo(secondTrack.Key));

            first.Phase = InteractionPhase.Cancel;
            HandSkeletonTrackGeometry removedTrack;
            Assert.That(geometry.TryApply(first, Surface(), out removedTrack), Is.False);
            Assert.That(removedTrack, Is.Null);
            Assert.That(geometry.Count, Is.EqualTo(1));
            Assert.That(geometry.Contains(secondTrack.Key), Is.True);
        }

        [Test]
        public void Clear_RemovesEveryTrackOnDisconnect()
        {
            var geometry = new HandSkeletonGeometry();
            for (var id = 1; id <= 8; id++)
            {
                var point = HandInteractionExtensionTests.PointWithHandExtension(
                    HandInteractionExtensionTests.CreateHandExtension());
                point.Id = id;
                HandSkeletonTrackGeometry ignored;
                Assert.That(geometry.TryApply(point, Surface(), out ignored), Is.True);
            }

            Assert.That(geometry.Count, Is.EqualTo(8));
            geometry.Clear();
            Assert.That(geometry.Count, Is.Zero);
        }

        [Test]
        public void RadarPointWithoutHandExtension_CreatesNoSkeletonGeometry()
        {
            var geometry = new HandSkeletonGeometry();
            var radar = new InteractionPoint
            {
                Id = 1,
                SurfaceId = "FRONT",
                ProviderId = "blaze.radar.f10f20",
                ProviderInstanceId = "radar-main",
                SourceId = "F1",
                Phase = InteractionPhase.Move,
                NormalizedPosition = new Vector2Data { X = .5f, Y = .5f },
                PixelPosition = new Vector2Data { X = 960f, Y = 540f },
                Confidence = .9f,
                TimestampUnixMs = 1000,
                Fp = new List<Vector2Data>()
            };

            HandSkeletonTrackGeometry ignored;
            Assert.That(geometry.TryApply(radar, Surface(), out ignored), Is.False);
            Assert.That(geometry.Count, Is.Zero);
        }

        private static InteractionSurface Surface()
        {
            return new InteractionSurface
            {
                SurfaceId = "FRONT",
                Name = "Front",
                LogicalWidth = 200,
                LogicalHeight = 100,
                IsPrimary = true,
                Order = 0
            };
        }
    }
}
