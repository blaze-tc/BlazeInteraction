using System;
using System.Collections.Generic;
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
    }
}
