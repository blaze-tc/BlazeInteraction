using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blaze.Interaction.Tests
{
    public sealed class InteractionCameraRouterTests
    {
        private readonly List<Object> createdObjects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < createdObjects.Count; index++)
            {
                var gameObject = createdObjects[index] as GameObject;
                if (gameObject == null)
                {
                    continue;
                }

                var camera = gameObject.GetComponent<Camera>();
                if (camera != null)
                {
                    camera.targetTexture = null;
                }
            }

            for (var index = createdObjects.Count - 1; index >= 0; index--)
            {
                var value = createdObjects[index];
                if (value != null)
                {
                    Object.DestroyImmediate(value);
                }
            }

            createdObjects.Clear();
        }

        [Test]
        public void TryGetCamera_IsCaseInsensitiveAndUnknownIdsFail()
        {
            var camera = CreateCamera("FrontCamera");
            var router = CreateRouter(Binding("front", camera));

            Assert.That(router.TryGetCamera("FRONT", out var resolved), Is.True);
            Assert.That(resolved, Is.SameAs(camera));
            Assert.That(router.TryGetCamera("missing", out _), Is.False);
        }

        [Test]
        public void TryMapToCameraPixel_MapsLogicalPixelIntoOffsetPixelRect()
        {
            var camera = CreateCamera("FrontCamera");
            camera.pixelRect = new Rect(100f, 50f, 800f, 600f);
            var router = CreateRouter(Binding("front", camera));

            var mapped = router.TryMapToCameraPixel(
                Surface("front", 1920, 1080),
                Point("front", 960f, 540f),
                out var pixel);

            Assert.That(mapped, Is.True);
            Assert.That(pixel.x, Is.EqualTo(500f).Within(.01f));
            Assert.That(pixel.y, Is.EqualTo(350f).Within(.01f));
        }

        [Test]
        public void TryMapToCameraPixel_RenderTextureUsesTexturePixelsAndZeroOrigin()
        {
            var camera = CreateCamera("TextureCamera");
            var texture = new RenderTexture(1024, 512, 16);
            texture.Create();
            createdObjects.Add(texture);
            camera.targetTexture = texture;
            var router = CreateRouter(Binding("wall", camera));

            Assert.That(router.TryMapToCameraPixel(
                Surface("wall", 2048, 1024),
                Point("wall", 512f, 256f),
                out var pixel), Is.True);
            Assert.That(pixel.x, Is.EqualTo(256f).Within(.01f));
            Assert.That(pixel.y, Is.EqualTo(128f).Within(.01f));
        }

        [Test]
        public void TryCreateRay_CenterPixelUsesBoundCameraForward()
        {
            var camera = CreateCamera("FrontCamera");
            camera.transform.position = new Vector3(1f, 2f, -5f);
            camera.transform.rotation = Quaternion.identity;
            camera.pixelRect = new Rect(0f, 0f, 800f, 600f);
            var router = CreateRouter(Binding("front", camera));

            Assert.That(router.TryCreateRay(
                Surface("front", 1600, 1200),
                Point("front", 800f, 600f),
                out var ray), Is.True);
            Assert.That(Vector3.Dot(ray.direction.normalized, camera.transform.forward), Is.GreaterThan(.999f));
        }

        [Test]
        public void TryRaycast_HonorsLayerMaskAndMaximumDistance()
        {
            var camera = CreateCamera("FrontCamera");
            camera.transform.position = Vector3.zero;
            camera.transform.rotation = Quaternion.identity;
            camera.pixelRect = new Rect(0f, 0f, 800f, 600f);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall";
            wall.layer = 8;
            wall.transform.position = new Vector3(0f, 0f, 10f);
            createdObjects.Add(wall);
            Physics.SyncTransforms();
            var binding = Binding("front", camera);
            binding.raycastLayerMask = 1 << 8;
            binding.maximumRayDistance = 20f;
            var router = CreateRouter(binding);

            Assert.That(router.TryRaycast(
                Surface("front", 800, 600),
                Point("front", 400f, 300f),
                out var hit), Is.True);
            Assert.That(hit.collider.gameObject, Is.SameAs(wall));
        }

        [Test]
        public void DifferentSurfaces_RouteToIndependentCameras()
        {
            var front = CreateCamera("Front");
            var left = CreateCamera("Left");
            var router = CreateRouter(Binding("front", front), Binding("left", left));

            Assert.That(router.TryGetCamera("front", out var frontResult), Is.True);
            Assert.That(router.TryGetCamera("left", out var leftResult), Is.True);
            Assert.That(frontResult, Is.SameAs(front));
            Assert.That(leftResult, Is.SameAs(left));
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
            camera.pixelRect = new Rect(0f, 0f, 800f, 600f);
            var router = CreateRouter(Binding("front", camera));

            Assert.That(router.TryMapToCameraPixel(
                Surface("front", 0, 1080), Point("front", 1f, 1f), out _), Is.False);
            Assert.That(router.TryMapToCameraPixel(
                Surface("front", 1920, 1080), Point("front", float.NaN, 1f), out _), Is.False);
            Assert.That(router.TryMapToCameraPixel(
                Surface("front", 1920, 1080), Point("front", 1f, float.PositiveInfinity), out _), Is.False);
        }

        [Test]
        public void RepeatedInvalidLookupWarnsOnlyOncePerStableErrorKey()
        {
            var router = CreateRouter(Binding("front", CreateCamera("FrontCamera")));
            var before = router.WarningCountForTests;

            Assert.That(router.TryGetCamera("missing", out _), Is.False);
            Assert.That(router.TryGetCamera("missing", out _), Is.False);
            Assert.That(router.WarningCountForTests, Is.EqualTo(before + 1));
            Assert.That(router.TryGetCamera("other", out _), Is.False);
            Assert.That(router.WarningCountForTests, Is.EqualTo(before + 2));
        }

        private InteractionCameraRouter CreateRouter(params InteractionSurfaceCameraBinding[] bindings)
        {
            var gameObject = new GameObject("InteractionCameraRouterTests");
            createdObjects.Add(gameObject);
            var router = gameObject.AddComponent<InteractionCameraRouter>();
            router.ConfigureForTests(bindings);
            return router;
        }

        private Camera CreateCamera(string name)
        {
            var gameObject = new GameObject(name);
            createdObjects.Add(gameObject);
            return gameObject.AddComponent<Camera>();
        }

        private static InteractionSurfaceCameraBinding Binding(string surfaceId, Camera camera)
        {
            return new InteractionSurfaceCameraBinding
            {
                surfaceId = surfaceId,
                camera = camera,
                raycastLayerMask = ~0,
                maximumRayDistance = 1000f
            };
        }

        private static InteractionSurface Surface(string id, int width, int height)
        {
            return new InteractionSurface
            {
                SurfaceId = id,
                Name = id,
                LogicalWidth = width,
                LogicalHeight = height,
                IsPrimary = id == "front",
                Order = 0
            };
        }

        private static InteractionPoint Point(string surfaceId, float pixelX, float pixelY)
        {
            return new InteractionPoint
            {
                Id = 1,
                SurfaceId = surfaceId,
                ProviderId = "blaze.test",
                ProviderInstanceId = "test-main",
                SourceId = "source",
                Phase = InteractionPhase.Move,
                NormalizedPosition = new Vector2Data { X = .5f, Y = .5f },
                PixelPosition = new Vector2Data { X = pixelX, Y = pixelY },
                Confidence = 1f,
                TimestampUnixMs = 1
            };
        }
    }
}
