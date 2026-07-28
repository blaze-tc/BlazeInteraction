using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Blaze.Radar.Internal;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blaze.Radar.Tests
{
    public sealed class RadarFrameDispatcherTests
    {
        [Test]
        public void Connect_OrdersEnabledScreensAndOmitsDisabledScreens()
        {
            using (var fixture = Fixture(
                Screen("right", false, false, 1),
                Screen("front", true, true, 1),
                Screen("left", true, false, 0)))
            {
                fixture.Dispatcher.Connect();

                CollectionAssert.AreEqual(
                    new[] { "left", "front" },
                    fixture.Client.StartedHello.screens.ConvertAll(screen => screen.screenId));
                Assert.That(fixture.Client.StartedHello.screens[0].defaultWidthPixels, Is.EqualTo(1920));
            }
        }

        [Test]
        public void Batch_DispatchesFramesAndPointersInOrderOnCallingThread()
        {
            using (var fixture = Fixture(Screen("front", true, true, 0), Screen("right", true, false, 1)))
            {
                var frameIds = new List<string>();
                var pointers = new List<string>();
                var callbackThreads = new List<int>();
                fixture.Dispatcher.ScreenFrameReceived += frame =>
                {
                    frameIds.Add(frame.screen.screenId);
                    callbackThreads.Add(Thread.CurrentThread.ManagedThreadId);
                };
                fixture.Dispatcher.ScreenPointerReceived += (screen, pointer) =>
                {
                    pointers.Add(screen.screenId + ":" + pointer.pointerId);
                    callbackThreads.Add(Thread.CurrentThread.ManagedThreadId);
                };
                fixture.Client.Publish(Batch(
                    Frame("right", 1, Pointer(3)),
                    Frame("front", 2, Pointer(7), Pointer(8))));
                var callingThread = Thread.CurrentThread.ManagedThreadId;

                fixture.Dispatcher.TickForTests();

                CollectionAssert.AreEqual(new[] { "right", "front" }, frameIds);
                CollectionAssert.AreEqual(new[] { "right:3", "front:7", "front:8" }, pointers);
                Assert.That(callbackThreads, Is.All.EqualTo(callingThread));
            }
        }

        [Test]
        public void UnityStall_DrainsLifecycleAndLatestVisualBatchInOrderWithinOneBoundedTick()
        {
            using (var fixture = Fixture(Screen("front", true, true, 0)))
            {
                var frames = new List<RadarScreenPointerFrame>();
                fixture.Dispatcher.ScreenFrameReceived += frames.Add;
                fixture.Client.Publish(Batch(Frame("front", 1, Pointer(7, RadarPointerPhase.Down))));
                fixture.Client.Publish(Batch(Frame("front", 2, Pointer(7, RadarPointerPhase.Move))));
                fixture.Client.Publish(Batch(Frame("front", 3, Pointer(7, RadarPointerPhase.Up))));
                fixture.Client.Publish(Batch(Frame("front", 4)));

                fixture.Dispatcher.TickForTests();

                CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, frames.ConvertAll(frame => frame.sequence));
                CollectionAssert.AreEqual(
                    new[] { RadarPointerPhase.Down, RadarPointerPhase.Move, RadarPointerPhase.Up },
                    frames.GetRange(0, 3).ConvertAll(frame => frame.pointers[0].phase));
                Assert.That(frames[3].pointers, Is.Empty);
            }
        }

        [Test]
        public void Batch_OnlyPrimaryScreenRaisesLegacyEventAndSetsLatestFrame()
        {
            using (var fixture = Fixture(Screen("left", true, false, 0), Screen("front", true, true, 1)))
            {
                var legacyFrames = new List<RadarPointerFrameMessage>();
                fixture.Dispatcher.PointerFrameReceived += legacyFrames.Add;
                fixture.Client.Publish(Batch(Frame("left", 10, Pointer(1)), Frame("front", 11, Pointer(7))));

                fixture.Dispatcher.TickForTests();

                var legacy = AssertOne(legacyFrames);
                Assert.That(legacy.sequence, Is.EqualTo(11));
                Assert.That(legacy.timestampUnixMilliseconds, Is.EqualTo(1011));
                Assert.That(legacy.pointers, Has.Count.EqualTo(1));
                Assert.That(legacy.pointers[0].pointerId, Is.EqualTo(7));
                Assert.That(fixture.Dispatcher.LatestFrame, Is.SameAs(legacy));
            }
        }

        [Test]
        public void LatestScreenFrames_HoldsSeparateEntriesAndIsGenuinelyReadOnly()
        {
            using (var fixture = Fixture(Screen("left", true, false, 0), Screen("front", true, true, 1)))
            {
                fixture.Client.Publish(Batch(Frame("left", 1), Frame("front", 2)));
                fixture.Dispatcher.TickForTests();

                Assert.That(fixture.Dispatcher.LatestScreenFrames.Keys, Is.EquivalentTo(new[] { "left", "front" }));
                var mutable = fixture.Dispatcher.LatestScreenFrames as IDictionary<string, RadarScreenPointerFrame>;
                Assert.That(mutable, Is.Not.Null);
                Assert.Throws<NotSupportedException>(() => mutable.Clear());
                Assert.That(fixture.Dispatcher.LatestScreenFrames, Has.Count.EqualTo(2));
            }
        }

        [Test]
        public void MalformedEntries_DoNotPreventLaterValidFrameOrPointer()
        {
            using (var fixture = Fixture(Screen("front", true, true, 0)))
            {
                var pointers = new List<int>();
                var errors = new List<string>();
                fixture.Dispatcher.ScreenPointerReceived += (_, pointer) => pointers.Add(pointer.pointerId);
                fixture.Dispatcher.ErrorReceived += errors.Add;
                fixture.Client.Publish(new RadarPointerBatchPayload
                {
                    screens = new List<RadarScreenPointerFrame>
                    {
                        null,
                        new RadarScreenPointerFrame { screen = null },
                        new RadarScreenPointerFrame
                        {
                            screen = Info("front"),
                            sequence = 3,
                            pointers = new List<RadarScreenPointer> { null, Pointer(9) }
                        }
                    }
                });

                Assert.DoesNotThrow(fixture.Dispatcher.TickForTests);

                CollectionAssert.AreEqual(new[] { 9 }, pointers);
                Assert.That(errors, Has.Count.GreaterThanOrEqualTo(3));
                Assert.That(fixture.Dispatcher.LatestScreenFrames.ContainsKey("front"), Is.True);
            }
        }

        [Test]
        public void ThrowingSubscriber_DoesNotBlockLaterSubscriber()
        {
            using (var fixture = Fixture(Screen("front", true, true, 0)))
            {
                var laterFrame = false;
                var laterPointer = false;
                fixture.Dispatcher.ScreenFrameReceived += _ => throw new InvalidOperationException("frame observer");
                fixture.Dispatcher.ScreenFrameReceived += _ => laterFrame = true;
                fixture.Dispatcher.ScreenPointerReceived += (_, __) => throw new InvalidOperationException("pointer observer");
                fixture.Dispatcher.ScreenPointerReceived += (_, __) => laterPointer = true;
                fixture.Client.Publish(Batch(Frame("front", 1, Pointer(1))));

                Assert.DoesNotThrow(fixture.Dispatcher.TickForTests);
                Assert.That(laterFrame, Is.True);
                Assert.That(laterPointer, Is.True);
            }
        }

        [Test]
        public void InvalidTopology_ReportsEveryErrorAndDoesNotStartClient()
        {
            using (var fixture = Fixture(Screen("INVALID ID", true, false, 0)))
            {
                var errors = new List<string>();
                fixture.Dispatcher.ErrorReceived += errors.Add;

                fixture.Dispatcher.Connect();

                Assert.That(fixture.Client.StartCount, Is.Zero);
                Assert.That(errors, Has.Count.GreaterThanOrEqualTo(2));
                Assert.That(string.Join("\n", errors), Does.Contain("screenId").And.Contain("primary").IgnoreCase);
            }
        }

        [Test]
        public void DisconnectThenConnect_IsSupportedWithoutDestroyingDispatcher()
        {
            using (var fixture = Fixture(Screen("front", true, true, 0)))
            {
                fixture.Dispatcher.Connect();
                fixture.Dispatcher.DisconnectAsync().GetAwaiter().GetResult();
                fixture.Dispatcher.Connect();

                Assert.That(fixture.Client.StopCount, Is.EqualTo(1));
                Assert.That(fixture.Client.StartCount, Is.EqualTo(2));
            }
        }

        private static DispatcherFixture Fixture(params RadarScreenDefinition[] screens)
        {
            var settings = ScriptableObject.CreateInstance<RadarRuntimeSettings>();
            SetField(settings, "screens", new List<RadarScreenDefinition>(screens));
            SetField(settings, "screenTopologySchemaVersion", 1);
            var gameObject = new GameObject("RadarFrameDispatcherTests");
            var dispatcher = gameObject.AddComponent<RadarFrameDispatcher>();
            var client = new FakeRadarPipeClient();
            dispatcher.ConfigureForTests(settings, client, false);
            return new DispatcherFixture(gameObject, settings, dispatcher, client);
        }

        private static RadarScreenDefinition Screen(string id, bool enabled, bool primary, int order)
        {
            return new RadarScreenDefinition(id, id, 1920, 1080, enabled, primary, order);
        }

        private static RadarPointerBatchPayload Batch(params RadarScreenPointerFrame[] frames)
        {
            return new RadarPointerBatchPayload { screens = new List<RadarScreenPointerFrame>(frames) };
        }

        private static RadarScreenPointerFrame Frame(string id, long sequence, params RadarScreenPointer[] pointers)
        {
            return new RadarScreenPointerFrame
            {
                screen = Info(id),
                sequence = sequence,
                timestampUnixMilliseconds = 1000 + sequence,
                pointers = new List<RadarScreenPointer>(pointers)
            };
        }

        private static RadarScreenInfo Info(string id)
        {
            return new RadarScreenInfo
            {
                screenId = id,
                name = id,
                widthPixels = 1920,
                heightPixels = 1080,
                isPrimary = id == "front",
                order = 0
            };
        }

        private static RadarScreenPointer Pointer(int id, RadarPointerPhase phase = RadarPointerPhase.Move)
        {
            return new RadarScreenPointer
            {
                pointerId = id,
                phase = phase,
                normalizedX = 0.25f,
                normalizedY = 0.75f,
                pixelX = 480,
                pixelY = 810,
                confidence = 0.9f,
                timestampUnixMilliseconds = 2000 + id
            };
        }

        private static T AssertOne<T>(IList<T> values)
        {
            Assert.That(values, Has.Count.EqualTo(1));
            return values[0];
        }

        private static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            field.SetValue(target, value);
        }

        private sealed class DispatcherFixture : IDisposable
        {
            private readonly GameObject gameObject;
            private readonly RadarRuntimeSettings settings;

            public DispatcherFixture(
                GameObject gameObject,
                RadarRuntimeSettings settings,
                RadarFrameDispatcher dispatcher,
                FakeRadarPipeClient client)
            {
                this.gameObject = gameObject;
                this.settings = settings;
                Dispatcher = dispatcher;
                Client = client;
            }

            public RadarFrameDispatcher Dispatcher { get; private set; }
            public FakeRadarPipeClient Client { get; private set; }

            public void Dispose()
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(settings);
            }
        }

        private sealed class FakeRadarPipeClient : IRadarPipeClient
        {
            private readonly LifecycleBatchBuffer batches = new LifecycleBatchBuffer();

            public bool IsConnected { get; private set; }
            public long DroppedBatchCount { get { return batches.DroppedCount; } }
            public RadarHelloPayload StartedHello { get; private set; }
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }

            public event Action<bool> ConnectionChanged;
            public event Action<string> ErrorReceived;

            public void Start(RadarHelloPayload hello)
            {
                StartedHello = hello;
                StartCount++;
                IsConnected = true;
            }

            public bool TryConsumeLatestBatch(out RadarPointerBatchPayload batch)
            {
                return batches.TryConsume(out batch);
            }

            public void DrainMainThreadEvents()
            {
            }

            public Task StopAsync()
            {
                StopCount++;
                IsConnected = false;
                batches.Clear();
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                IsConnected = false;
                batches.Clear();
            }

            public void Publish(RadarPointerBatchPayload batch)
            {
                batches.Publish(batch);
            }
        }
    }
}
