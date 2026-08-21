using System.Collections.Generic;
using NUnit.Framework;

namespace Blaze.Interaction.Tests
{
    public sealed class InteractionFrameDispatcherTests
    {
        [Test]
        public void Frames_UpdateOneStablePointsViewAndRaiseLifecycleEvents()
        {
            var dispatcher = new InteractionFrameDispatcher();
            var points = dispatcher.Points;
            var events = new List<string>();
            dispatcher.PointAdded += point => events.Add("add:" + point.Id);
            dispatcher.PointUpdated += point => events.Add("update:" + point.Id);
            dispatcher.PointRemoved += point => events.Add("remove:" + point.Id + ":" + point.Phase);

            dispatcher.ApplyFrame(Frame(1, Point(7, InteractionPhase.Down, 100f)));
            dispatcher.ApplyFrame(Frame(2, Point(7, InteractionPhase.Move, 200f)));
            dispatcher.ApplyFrame(Frame(3, Point(7, InteractionPhase.Up, 200f)));

            Assert.That(dispatcher.Points, Is.SameAs(points));
            Assert.That(points, Is.Empty);
            Assert.That(events, Is.EqualTo(new[] { "add:7", "update:7", "remove:7:Up" }));
        }

        [Test]
        public void ProviderChanged_CancelsEveryPointBeforePublishingProviderEvent()
        {
            var dispatcher = new InteractionFrameDispatcher();
            var events = new List<string>();
            dispatcher.ApplyHelloAck(new HelloAckPayload
            {
                BridgeVersion = "1.0.0",
                ActiveProvider = Provider("blaze.radar.f10f20", "radar-main")
            });
            dispatcher.ApplyFrame(Frame(1, Point(1, InteractionPhase.Down, 100f), Point(2, InteractionPhase.Down, 200f)));
            dispatcher.PointRemoved += point => events.Add("cancel:" + point.Id + ":" + point.Phase);
            dispatcher.ProviderChanged += _ => events.Add("provider");

            dispatcher.ApplyProviderChanged(new ProviderChangedPayload
            {
                PreviousProvider = Provider("blaze.radar.f10f20", "radar-main"),
                ActiveProvider = Provider("blaze.test", "test-main"),
                TimestampUnixMs = 2000
            });

            Assert.That(dispatcher.Points, Is.Empty);
            Assert.That(dispatcher.ActiveProvider.InstanceId, Is.EqualTo("test-main"));
            Assert.That(events, Is.EqualTo(new[] { "cancel:1:Cancel", "cancel:2:Cancel", "provider" }));
        }

        [Test]
        public void Disconnect_CancelsPointsAndResetsConnectionAndProviderState()
        {
            var dispatcher = new InteractionFrameDispatcher();
            dispatcher.ApplyHelloAck(new HelloAckPayload
            {
                BridgeVersion = "1.0.0",
                ActiveProvider = Provider("blaze.radar.f10f20", "radar-main")
            });
            dispatcher.SetConnectionState(true);
            dispatcher.ApplyFrame(Frame(1, Point(7, InteractionPhase.Down, 100f)));
            InteractionPhase? removedPhase = null;
            dispatcher.PointRemoved += point => removedPhase = point.Phase;

            dispatcher.SetConnectionState(false);

            Assert.That(dispatcher.IsConnected, Is.False);
            Assert.That(dispatcher.ActiveProvider, Is.Null);
            Assert.That(dispatcher.Points, Is.Empty);
            Assert.That(removedPhase, Is.EqualTo(InteractionPhase.Cancel));
        }

        [Test]
        public void SamePointId_OnDifferentSurfaces_IsTrackedAndRemovedIndependently()
        {
            var dispatcher = new InteractionFrameDispatcher();
            dispatcher.ApplyFrame(Frame(
                1,
                Point(7, "FRONT", InteractionPhase.Down, 100f),
                Point(7, "BACK", InteractionPhase.Down, 200f)));

            dispatcher.ApplyFrame(Frame(2, Point(7, "FRONT", InteractionPhase.Up, 100f)));

            Assert.That(dispatcher.Points, Has.Count.EqualTo(1));
            Assert.That(dispatcher.Points[0].Id, Is.EqualTo(7));
            Assert.That(dispatcher.Points[0].SurfaceId, Is.EqualTo("BACK"));
        }

        [Test]
        public void Manager_ExposesRequiredProviderNeutralRuntimeApi()
        {
            using (var manager = new InteractionManager("Blaze.InteractionBridge.Tests", 100, 25, 1000))
            {
                Assert.That(manager.Points, Is.Not.Null);
                Assert.That(manager.IsConnected, Is.False);
                Assert.That(manager.ActiveProvider, Is.Null);
                Assert.That(InteractionManager.Instance, Is.Not.Null);
            }
        }

        private static ProviderReferencePayload Provider(string id, string instanceId)
        {
            return new ProviderReferencePayload { Id = id, InstanceId = instanceId };
        }

        private static InteractionFrame Frame(long sequence, params InteractionPoint[] points)
        {
            return new InteractionFrame
            {
                ProviderId = "blaze.radar.f10f20",
                ProviderInstanceId = "radar-main",
                SurfaceId = "FRONT",
                Sequence = sequence,
                TimestampUnixMs = 1000 + sequence,
                Points = new List<InteractionPoint>(points)
            };
        }

        private static InteractionPoint Point(long id, InteractionPhase phase, float pixelX)
        {
            return Point(id, "FRONT", phase, pixelX);
        }

        private static InteractionPoint Point(
            long id,
            string surfaceId,
            InteractionPhase phase,
            float pixelX)
        {
            return new InteractionPoint
            {
                Id = id,
                SurfaceId = surfaceId,
                ProviderId = "blaze.radar.f10f20",
                ProviderInstanceId = "radar-main",
                SourceId = "sensor-1",
                Phase = phase,
                NormalizedPosition = new Vector2Data { X = .5f, Y = .25f },
                PixelPosition = new Vector2Data { X = pixelX, Y = 250f },
                Confidence = .9f,
                TimestampUnixMs = 1000
            };
        }
    }
}
