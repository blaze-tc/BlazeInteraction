using System.Collections.Generic;
using Blaze.Radar;
using NUnit.Framework;
using UnityEngine;

namespace Blaze.Interaction.Tests
{
    public sealed class RadarCompatibilityTests
    {
        [Test]
        public void LauncherAndInputModule_InheritTheSingleInteractionImplementations()
        {
            Assert.That(typeof(RadarBridgeLauncher).IsSubclassOf(typeof(InteractionBridgeLauncher)), Is.True);
            Assert.That(typeof(RadarInputModule).IsSubclassOf(typeof(InteractionInputModule)), Is.True);
        }

        [Test]
        public void Dispatcher_AdaptsOneInteractionFrameWithoutChangingBatchOrSurfaceSemantics()
        {
            var gameObject = new GameObject("RadarCompatibilityDispatcherTests");
            gameObject.SetActive(false);
            var dispatcher = gameObject.AddComponent<RadarFrameDispatcher>();
            var received = new List<RadarScreenPointer>();
            var frames = new List<RadarScreenPointerFrame>();
            dispatcher.ScreenPointerReceived += (_, point) => received.Add(point);
            dispatcher.ScreenFrameReceived += frame => frames.Add(frame);
            try
            {
                var frame = new InteractionFrame
                {
                    ProviderId = "blaze.radar.f10f20",
                    ProviderInstanceId = "radar-main",
                    SurfaceId = "front",
                    Sequence = 42,
                    TimestampUnixMs = 1234,
                    Points = new List<InteractionPoint>
                    {
                        Point(7, "sensor-1", InteractionPhase.Down, 480f, 810f),
                        Point(8, "sensor-2", InteractionPhase.Hover, 960f, 540f)
                    }
                };
                var surface = new InteractionSurface
                {
                    SurfaceId = "front",
                    Name = "Front Wall",
                    LogicalWidth = 1920,
                    LogicalHeight = 1080,
                    IsPrimary = true,
                    Order = 3
                };

                dispatcher.ApplyInteractionFrameForTests(frame, surface);

                Assert.That(received, Has.Count.EqualTo(2));
                Assert.That(frames, Has.Count.EqualTo(1));
                Assert.That(frames[0].sequence, Is.EqualTo(42));
                Assert.That(frames[0].timestampUnixMilliseconds, Is.EqualTo(1234));
                Assert.That(frames[0].screen.name, Is.EqualTo("Front Wall"));
                Assert.That(frames[0].screen.widthPixels, Is.EqualTo(1920));
                Assert.That(frames[0].screen.heightPixels, Is.EqualTo(1080));
                Assert.That(frames[0].screen.isPrimary, Is.True);
                Assert.That(frames[0].screen.order, Is.EqualTo(3));
                Assert.That(frames[0].pointers, Has.Count.EqualTo(2));
                Assert.That(received[0].pointerId, Is.EqualTo(7));
                Assert.That(received[0].phase, Is.EqualTo(RadarPointerPhase.Down));
                Assert.That(received[0].normalizedX, Is.EqualTo(0.25f));
                Assert.That(received[0].sourceId, Is.EqualTo("sensor-1"));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        private static InteractionPoint Point(
            long id,
            string sourceId,
            InteractionPhase phase,
            float pixelX,
            float pixelY)
        {
            return new InteractionPoint
            {
                Id = id,
                ProviderId = "blaze.radar.f10f20",
                ProviderInstanceId = "radar-main",
                SourceId = sourceId,
                SurfaceId = "front",
                Phase = phase,
                NormalizedPosition = new Vector2Data { X = pixelX / 1920f, Y = pixelY / 1080f },
                PixelPosition = new Vector2Data { X = pixelX, Y = pixelY },
                Confidence = 0.9f,
                TimestampUnixMs = 1234
            };
        }
    }
}
