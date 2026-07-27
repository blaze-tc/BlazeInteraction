using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blaze.Radar.Tests
{
    public sealed class RadarScreenCameraRouterTests
    {
        private readonly List<Object> createdObjects = new List<Object>();
        private readonly List<RenderTexture> createdTextures = new List<RenderTexture>();

        [TearDown]
        public void TearDown()
        {
            for (var index = createdObjects.Count - 1; index >= 0; index--)
            {
                if (createdObjects[index] != null)
                {
                    Object.DestroyImmediate(createdObjects[index]);
                }
            }

            for (var index = 0; index < createdTextures.Count; index++)
            {
                var texture = createdTextures[index];
                if (texture != null)
                {
                    texture.Release();
                    Object.DestroyImmediate(texture);
                }
            }

            createdTextures.Clear();
            createdObjects.Clear();
        }

        [Test]
        public void TryGetCamera_IsCaseInsensitiveAndUnknownIdsFail()
        {
            var camera = CreateCamera("FrontCamera");
            var router = CreateRouter(Binding("front", camera));

            Assert.That(router.TryGetCamera("FRONT", out var actual), Is.True);
            Assert.That(actual, Is.SameAs(camera));
            Assert.That(router.TryGetCamera("missing", out _), Is.False);
        }

        [Test]
        public void TryMapToCameraPixel_MapsLogicalPixelIntoOffsetPixelRect()
        {
            var camera = CreateCamera("FrontCamera");
            camera.pixelRect = new Rect(100f, 50f, 800f, 600f);
            var router = CreateRouter(Binding("front", camera));

            Assert.That(
                router.TryMapToCameraPixel(Screen("front", 4000, 2000), Pointer(2000f, 500f), out var pixel),
                Is.True);
            Assert.That(pixel.x, Is.EqualTo(500f).Within(0.001f));
            Assert.That(pixel.y, Is.EqualTo(200f).Within(0.001f));
        }

        [Test]
        public void TryMapToCameraPixel_RenderTextureUsesTexturePixelsAndZeroOrigin()
        {
            var camera = CreateCamera("RightCamera");
            camera.pixelRect = new Rect(200f, 100f, 320f, 240f);
            camera.targetDisplay = 3;
            camera.targetTexture = CreateTexture(1024, 512);
            var router = CreateRouter(Binding("right", camera));

            Assert.That(
                router.TryMapToCameraPixel(Screen("right", 1920, 1440), Pointer(960f, 720f), out var pixel),
                Is.True);
            Assert.That(pixel.x, Is.EqualTo(512f).Within(0.001f));
            Assert.That(pixel.y, Is.EqualTo(256f).Within(0.001f));
        }

        [Test]
        public void TryCreateRay_CenterPixelUsesBoundCameraForward()
        {
            var camera = CreateCamera("FrontCamera");
            camera.transform.SetPositionAndRotation(new Vector3(2f, 3f, -5f), Quaternion.Euler(0f, 15f, 0f));
            camera.targetTexture = CreateTexture(800, 600);
            var router = CreateRouter(Binding("front", camera));

            Assert.That(
                router.TryCreateRay(Screen("front", 4000, 2000), Pointer(2000f, 1000f), out var ray),
                Is.True);
            Assert.That(Vector3.Angle(ray.direction, camera.transform.forward), Is.LessThan(0.01f));
            Assert.That(Vector3.Distance(ray.origin, camera.transform.position), Is.LessThan(1f));
        }

        [Test]
        public void TryRaycast_RenderTextureRayHonorsLayerMaskAndMaximumDistance()
        {
            var camera = CreateCamera("RightCamera");
            camera.transform.SetPositionAndRotation(new Vector3(0f, 0f, -10f), Quaternion.identity);
            camera.targetTexture = CreateTexture(1024, 512);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "RightWall";
            wall.layer = 8;
            wall.transform.localScale = new Vector3(8f, 6f, 0.5f);
            createdObjects.Add(wall);
            Physics.SyncTransforms();

            var binding = Binding("right", camera);
            binding.raycastLayerMask = 1 << 8;
            binding.maximumRayDistance = 20f;
            var router = CreateRouter(binding);

            Assert.That(
                router.TryRaycast(Screen("right", 1920, 1440), Pointer(960f, 720f), out var hit),
                Is.True);
            Assert.That(hit.collider.gameObject, Is.SameAs(wall));

            binding.raycastLayerMask = 1 << 9;
            router.ConfigureForTests(new[] { binding });
            Assert.That(router.TryRaycast(Screen("right", 1920, 1440), Pointer(960f, 720f), out _), Is.False);

            binding.raycastLayerMask = 1 << 8;
            binding.maximumRayDistance = 5f;
            router.ConfigureForTests(new[] { binding });
            Assert.That(router.TryRaycast(Screen("right", 1920, 1440), Pointer(960f, 720f), out _), Is.False);
        }

        [Test]
        public void ThreeScreenBindings_CenterRaysHitOnlyTheirCorrespondingWalls()
        {
            var leftCamera = CreateCamera("LeftCamera");
            var frontCamera = CreateCamera("FrontCamera");
            var rightCamera = CreateCamera("RightCamera");
            leftCamera.transform.position = new Vector3(-6f, 0f, -10f);
            frontCamera.transform.position = new Vector3(0f, 0f, -10f);
            rightCamera.transform.position = new Vector3(6f, 0f, -10f);
            leftCamera.targetTexture = CreateTexture(640, 480);
            frontCamera.targetTexture = CreateTexture(1280, 480);
            rightCamera.targetTexture = CreateTexture(640, 480);

            var leftWall = CreateWall("LeftWall", new Vector3(-6f, 0f, 0f));
            var frontWall = CreateWall("FrontWall", new Vector3(0f, 0f, 0f));
            var rightWall = CreateWall("RightWall", new Vector3(6f, 0f, 0f));
            Physics.SyncTransforms();

            var router = CreateRouter(
                Binding("left", leftCamera),
                Binding("front", frontCamera),
                Binding("right", rightCamera));

            AssertCenterHit(router, Screen("left", 1920, 1440), leftWall);
            AssertCenterHit(router, Screen("front", 4096, 1536), frontWall);
            AssertCenterHit(router, Screen("right", 1920, 1440), rightWall);
        }

        [Test]
        public void ImportedSampleSimulator_UsesDownMoveSingleUpAndPermanentFrontPair()
        {
            var simulatorType = FindImportedSampleType("Blaze.Radar.Samples.RadarLocalScreenSimulator");
            var gameObject = new GameObject("RadarLocalScreenSimulatorTests");
            createdObjects.Add(gameObject);
            var simulator = gameObject.AddComponent(simulatorType);

            var first = InvokeBatch(simulator, "BuildBatch", 0f, 1L);
            var moving = InvokeBatch(simulator, "BuildBatch", 1f / 30f, 2L);
            var sideUp = InvokeBatch(simulator, "BuildBatch", 6f, 181L);
            var sideRemoved = InvokeBatch(simulator, "BuildBatch", 6f + 1f / 30f, 182L);
            var shutdown = InvokeBatch(simulator, "BuildShutdownBatch", 183L);
            var repeatedShutdown = InvokeBatch(simulator, "BuildShutdownBatch", 184L);

            AssertPhases(first, "left", RadarPointerPhase.Down);
            AssertPhases(first, "front", RadarPointerPhase.Down, RadarPointerPhase.Down);
            AssertPhases(first, "right", RadarPointerPhase.Down);
            AssertPhases(moving, "left", RadarPointerPhase.Move);
            AssertPhases(moving, "front", RadarPointerPhase.Move, RadarPointerPhase.Move);
            AssertPhases(moving, "right", RadarPointerPhase.Move);
            AssertPhases(sideUp, "left", RadarPointerPhase.Up);
            AssertPhases(sideUp, "front", RadarPointerPhase.Move, RadarPointerPhase.Move);
            AssertPhases(sideUp, "right", RadarPointerPhase.Up);
            AssertPhases(sideRemoved, "left");
            AssertPhases(sideRemoved, "front", RadarPointerPhase.Move, RadarPointerPhase.Move);
            AssertPhases(sideRemoved, "right");
            AssertPhases(shutdown, "front", RadarPointerPhase.Up, RadarPointerPhase.Up);
            Assert.That(CountPointers(repeatedShutdown), Is.Zero, "Shutdown must not emit a second Up.");
        }

        [Test]
        public void ImportedSampleParticlePool_PrewarmsCapsAndReusesByScreenPointerKey()
        {
            var visualizerType = FindImportedSampleType("Blaze.Radar.Samples.RadarWorldPointerVisualizer");
            var visualizerObject = new GameObject("RadarWorldPointerVisualizerTests");
            createdObjects.Add(visualizerObject);
            var visualizer = visualizerObject.AddComponent(visualizerType);
            var prefabObject = new GameObject("ParticlePrototype");
            createdObjects.Add(prefabObject);
            var prefab = prefabObject.AddComponent<ParticleSystem>();

            Invoke(visualizer, "ConfigurePoolForTests", prefab, 4, 5);
            Assert.That(ReadInt(visualizer, "PoolCountForTests"), Is.EqualTo(4));
            Assert.That(ReadInt(visualizer, "TotalCreatedForTests"), Is.EqualTo(4));

            var left = (ParticleSystem)Invoke(visualizer, "AcquireForTests", "left", 1);
            var front = (ParticleSystem)Invoke(visualizer, "AcquireForTests", "front", 1);
            Assert.That(left, Is.Not.Null);
            Assert.That(front, Is.Not.Null.And.Not.SameAs(left), "Pointer IDs are scoped by ScreenId.");
            Assert.That(ReadInt(visualizer, "ActiveCountForTests"), Is.EqualTo(2));

            Invoke(visualizer, "ReleaseForTests", "left", 1);
            var reused = (ParticleSystem)Invoke(visualizer, "AcquireForTests", "right", 8);
            Assert.That(reused, Is.SameAs(left));

            Invoke(visualizer, "AcquireForTests", "left", 2);
            Invoke(visualizer, "AcquireForTests", "front", 2);
            Assert.That(Invoke(visualizer, "AcquireForTests", "right", 2), Is.Not.Null);
            Assert.That(Invoke(visualizer, "AcquireForTests", "right", 3), Is.Null);
            Assert.That(ReadInt(visualizer, "TotalCreatedForTests"), Is.EqualTo(5));
        }

        [Test]
        public void InvalidBindingsAreRejectedAndDuplicatesRemainAmbiguous()
        {
            var first = CreateCamera("First");
            var second = CreateCamera("Second");
            var invalidDistance = Binding("bad-distance", first);
            invalidDistance.maximumRayDistance = 0f;
            var router = CreateRouter(
                Binding("front", first),
                Binding("FRONT", second),
                Binding(string.Empty, first),
                Binding("no-camera", null),
                invalidDistance);

            Assert.That(router.TryGetCamera("front", out _), Is.False);
            Assert.That(router.TryGetCamera("no-camera", out _), Is.False);
            Assert.That(router.TryGetCamera("bad-distance", out _), Is.False);
            Assert.That(router.WarningCountForTests, Is.GreaterThanOrEqualTo(4));
        }

        [Test]
        public void InvalidResolutionAndNonfiniteCoordinatesAreRejected()
        {
            var camera = CreateCamera("FrontCamera");
            var router = CreateRouter(Binding("front", camera));

            Assert.That(router.TryMapToCameraPixel(Screen("front", 0, 1080), Pointer(1f, 1f), out _), Is.False);
            Assert.That(router.TryMapToCameraPixel(Screen("front", 1920, 1080), Pointer(float.NaN, 1f), out _), Is.False);
            Assert.That(router.TryMapToCameraPixel(Screen("front", 1920, 1080), Pointer(1f, float.PositiveInfinity), out _), Is.False);
        }

        [Test]
        public void RepeatedInvalidLookupWarnsOnlyOncePerStableErrorKey()
        {
            var camera = CreateCamera("FrontCamera");
            var router = CreateRouter(Binding("front", camera));
            var before = router.WarningCountForTests;

            Assert.That(router.TryGetCamera("missing", out _), Is.False);
            Assert.That(router.TryGetCamera("missing", out _), Is.False);
            Assert.That(router.WarningCountForTests, Is.EqualTo(before + 1));

            Assert.That(router.TryGetCamera("other", out _), Is.False);
            Assert.That(router.WarningCountForTests, Is.EqualTo(before + 2));
        }

        [Test]
        public void PointerState_ScreenAwareApplyKeepsScreenAndLogicalPixels()
        {
            var state = new RadarPointerState();
            var screen = Screen("front", 4096, 1536);
            var pointer = Pointer(3072f, 384f);
            pointer.pointerId = 17;
            pointer.normalizedX = 0.75f;
            pointer.normalizedY = 0.25f;
            pointer.phase = RadarPointerPhase.Move;
            pointer.timestampUnixMilliseconds = 123456L;

            state.Apply(screen, pointer);

            Assert.That(state.ScreenId, Is.EqualTo("front"));
            Assert.That(state.PointerId, Is.EqualTo(17));
            Assert.That(state.NormalizedPosition, Is.EqualTo(new Vector2(0.75f, 0.25f)));
            Assert.That(state.LogicalPixelPosition, Is.EqualTo(new Vector2(3072f, 384f)));
            Assert.That(state.ScreenPosition, Is.EqualTo(state.LogicalPixelPosition));
            Assert.That(state.TimestampUnixMilliseconds, Is.EqualTo(123456L));
        }

        private RadarScreenCameraRouter CreateRouter(params RadarScreenCameraBinding[] bindings)
        {
            var gameObject = new GameObject("RadarScreenCameraRouterTests");
            createdObjects.Add(gameObject);
            var router = gameObject.AddComponent<RadarScreenCameraRouter>();
            router.ConfigureForTests(bindings);
            return router;
        }

        private Camera CreateCamera(string name)
        {
            var gameObject = new GameObject(name);
            createdObjects.Add(gameObject);
            return gameObject.AddComponent<Camera>();
        }

        private GameObject CreateWall(string name, Vector3 position)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.position = position;
            wall.transform.localScale = new Vector3(5f, 5f, 0.5f);
            createdObjects.Add(wall);
            return wall;
        }

        private RenderTexture CreateTexture(int width, int height)
        {
            var texture = new RenderTexture(width, height, 16);
            texture.Create();
            createdTextures.Add(texture);
            return texture;
        }

        private static RadarScreenCameraBinding Binding(string screenId, Camera camera)
        {
            return new RadarScreenCameraBinding
            {
                screenId = screenId,
                camera = camera,
                raycastLayerMask = ~0,
                maximumRayDistance = 1000f
            };
        }

        private static RadarScreenInfo Screen(string id, int width, int height)
        {
            return new RadarScreenInfo
            {
                screenId = id,
                name = id,
                widthPixels = width,
                heightPixels = height
            };
        }

        private static RadarScreenPointer Pointer(float pixelX, float pixelY)
        {
            return new RadarScreenPointer
            {
                pixelX = pixelX,
                pixelY = pixelY,
                normalizedX = 0.5f,
                normalizedY = 0.5f,
                confidence = 1f
            };
        }

        private static void AssertCenterHit(
            RadarScreenCameraRouter router,
            RadarScreenInfo screen,
            GameObject expectedWall)
        {
            var pointer = Pointer(screen.widthPixels * 0.5f, screen.heightPixels * 0.5f);
            Assert.That(router.TryRaycast(screen, pointer, out var hit), Is.True, screen.screenId);
            Assert.That(hit.collider.gameObject, Is.SameAs(expectedWall), screen.screenId);
        }

        private static Type FindImportedSampleType(string fullName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                var type = assemblies[index].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            Assert.Ignore("Import the Multi-Screen Camera Routing sample to exercise its runtime tests.");
            return null;
        }

        private static RadarPointerBatchPayload InvokeBatch(object target, string method, params object[] arguments)
        {
            return (RadarPointerBatchPayload)Invoke(target, method, arguments);
        }

        private static object Invoke(object target, string method, params object[] arguments)
        {
            var info = target.GetType().GetMethod(
                method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, method);
            return info.Invoke(target, arguments);
        }

        private static int ReadInt(object target, string property)
        {
            var info = target.GetType().GetProperty(
                property,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, property);
            return (int)info.GetValue(target, null);
        }

        private static void AssertPhases(
            RadarPointerBatchPayload batch,
            string screenId,
            params RadarPointerPhase[] phases)
        {
            var frame = FindFrame(batch, screenId);
            Assert.That(frame.pointers, Has.Count.EqualTo(phases.Length), screenId);
            for (var index = 0; index < phases.Length; index++)
            {
                Assert.That(frame.pointers[index].phase, Is.EqualTo(phases[index]), screenId + " pointer " + index);
            }
        }

        private static RadarScreenPointerFrame FindFrame(RadarPointerBatchPayload batch, string screenId)
        {
            Assert.That(batch, Is.Not.Null);
            Assert.That(batch.screens, Is.Not.Null);
            for (var index = 0; index < batch.screens.Count; index++)
            {
                var frame = batch.screens[index];
                if (frame != null && frame.screen != null &&
                    string.Equals(frame.screen.screenId, screenId, StringComparison.OrdinalIgnoreCase))
                {
                    return frame;
                }
            }

            Assert.Fail("Missing screen frame '" + screenId + "'.");
            return null;
        }

        private static int CountPointers(RadarPointerBatchPayload batch)
        {
            var total = 0;
            for (var index = 0; index < batch.screens.Count; index++)
            {
                total += batch.screens[index].pointers.Count;
            }

            return total;
        }
    }
}
