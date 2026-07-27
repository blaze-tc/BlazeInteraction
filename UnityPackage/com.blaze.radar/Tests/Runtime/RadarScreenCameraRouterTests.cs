using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
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
        public void ImportedSampleSimulator_SecondCycleRestartsSidesAndKeepsFrontPointerIdsStable()
        {
            var simulatorType = FindImportedSampleType("Blaze.Radar.Samples.RadarLocalScreenSimulator");
            var gameObject = new GameObject("RadarLocalScreenSimulatorCycleTests");
            createdObjects.Add(gameObject);
            var simulator = gameObject.AddComponent(simulatorType);

            var secondCycleStart = InvokeBatch(simulator, "BuildBatch", 7f, 211L);
            var secondCycleSideUp = InvokeBatch(simulator, "BuildBatch", 13f, 391L);

            AssertPhases(secondCycleStart, "left", RadarPointerPhase.Down);
            AssertPhases(secondCycleStart, "right", RadarPointerPhase.Down);
            AssertPhases(secondCycleSideUp, "left", RadarPointerPhase.Up);
            AssertPhases(secondCycleSideUp, "right", RadarPointerPhase.Up);
            AssertPointerIds(secondCycleStart, "front", 1, 2);
            AssertPointerIds(secondCycleSideUp, "front", 1, 2);
            AssertPhases(secondCycleStart, "front", RadarPointerPhase.Move, RadarPointerPhase.Move);
            AssertPhases(secondCycleSideUp, "front", RadarPointerPhase.Move, RadarPointerPhase.Move);
        }

        [UnityTest]
        public IEnumerator ImportedSampleModeTransitionCoordinator_LastRequestedModeWinsPendingDisconnectRace()
        {
            var coordinatorType = FindImportedSampleType("Blaze.Radar.Samples.LatestModeTransitionCoordinator");
            var coordinator = Activator.CreateInstance(coordinatorType);
            var pendingDisconnect = new TaskCompletionSource<bool>();
            var activations = new List<int>();
            var beginCount = 0;

            var bridgeTask = (Task)Invoke(
                coordinator,
                "RequestAsync",
                1,
                (Action)(() => beginCount++),
                (Func<Task>)(() => pendingDisconnect.Task),
                (Func<bool>)(() => true),
                (Action<int>)(mode => activations.Add(mode)));
            var localTask = (Task)Invoke(
                coordinator,
                "RequestAsync",
                0,
                (Action)(() => beginCount++),
                (Func<Task>)(() => Task.CompletedTask),
                (Func<bool>)(() => true),
                (Action<int>)(mode => activations.Add(mode)));

            Assert.That(localTask.IsCompleted, Is.True, "The completed latest transition should activate immediately.");
            CollectionAssert.AreEqual(new[] { 0 }, activations);
            pendingDisconnect.SetResult(true);
            while (!bridgeTask.IsCompleted)
            {
                yield return null;
            }

            Assert.That(beginCount, Is.EqualTo(2));
            CollectionAssert.AreEqual(new[] { 0 }, activations, "A stale Bridge completion must not replace Local.");
        }

        [Test]
        public void ImportedSamplePresenter_BeginTransitionLeavesNoActiveOrAcceptingSource()
        {
            var presenterType = FindImportedSampleType("Blaze.Radar.Samples.MultiScreenCameraRoutingPresenter");
            var presenterObject = new GameObject("MultiScreenCameraRoutingPresenterTransitionTests");
            createdObjects.Add(presenterObject);
            var presenter = presenterObject.AddComponent(presenterType);
            var modeType = presenterType.GetNestedType("DataSourceMode", BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(modeType, Is.Not.Null);
            var bridgeMode = Enum.ToObject(modeType, 1);

            Assert.That(ReadBool(presenter, "hasActiveMode"), Is.True);
            Invoke(presenter, "BeginModeTransition", bridgeMode);

            Assert.That(ReadBool(presenter, "hasActiveMode"), Is.False, "Pending disconnect must not advertise an active source.");
            Assert.That(ReadBool(presenter, "acceptingLocal"), Is.False);
            Assert.That(ReadBool(presenter, "acceptingIpc"), Is.False);
        }

        [Test]
        public void ImportedSampleScene_PreservesThreeScreenBindingsAndSettingsContract()
        {
            var scenePaths = Directory.GetFiles(
                Application.dataPath,
                "MultiScreenCameraRouting.unity",
                SearchOption.AllDirectories);
            if (scenePaths.Length == 0)
            {
                Assert.Ignore("Import the Multi-Screen Camera Routing sample to validate its serialized scene contract.");
            }

            var scenePath = Array.Find(
                scenePaths,
                path => path.IndexOf("Multi-Screen Camera Routing", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? scenePaths[0];
            var settingsPath = Path.Combine(Path.GetDirectoryName(scenePath), "MultiScreenRadarSettings.asset");
            Assert.That(File.Exists(settingsPath), Is.True, settingsPath);
            var scene = File.ReadAllText(scenePath);
            var settings = File.ReadAllText(settingsPath);

            var requiredObjects = new[]
            {
                "MultiScreenCameraRouting",
                "RadarFrameDispatcher",
                "RadarScreenCameraRouter",
                "MultiScreenCameraRoutingPresenter",
                "RadarLocalScreenSimulator",
                "RadarWorldPointerVisualizer",
                "RadarSampleLogPanel",
                "PointerParticlePool",
                "LeftCamera",
                "FrontCamera",
                "RightCamera",
                "LeftWall",
                "FrontWall",
                "RightWall"
            };
            for (var index = 0; index < requiredObjects.Length; index++)
            {
                StringAssert.Contains("m_Name: " + requiredObjects[index], scene, requiredObjects[index]);
            }

            StringAssert.Contains("autoConnect: 0", scene);
            StringAssert.Contains("dispatcher: {fileID: 1412215115}", scene);
            StringAssert.Contains("simulator: {fileID: 1840161209}", scene);
            StringAssert.Contains("visualizer: {fileID: 1579444146}", scene);
            StringAssert.Contains("logPanel: {fileID: 1305753124}", scene);
            StringAssert.Contains("- screenId: left\n    camera: {fileID: 1448918202}", NormalizeNewlines(scene));
            StringAssert.Contains("- screenId: front\n    camera: {fileID: 463494870}", NormalizeNewlines(scene));
            StringAssert.Contains("- screenId: right\n    camera: {fileID: 39084049}", NormalizeNewlines(scene));

            AssertSerializedScreen(settings, "left", 1920, 1440, false, 0);
            AssertSerializedScreen(settings, "front", 4096, 1536, true, 1);
            AssertSerializedScreen(settings, "right", 1920, 1440, false, 2);
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

        [UnityTest]
        public IEnumerator ImportedSampleParticlePool_DefaultsToSixteenAndHardCapsAtSixtyFour()
        {
            var visualizerType = FindImportedSampleType("Blaze.Radar.Samples.RadarWorldPointerVisualizer");
            var visualizerObject = new GameObject("RadarWorldPointerVisualizerDefaultPoolTests");
            visualizerObject.SetActive(false);
            createdObjects.Add(visualizerObject);
            var visualizer = visualizerObject.AddComponent(visualizerType);
            var prefabObject = new GameObject("ParticlePrototype");
            createdObjects.Add(prefabObject);
            var prefab = prefabObject.AddComponent<ParticleSystem>();
            SetField(visualizer, "particlePrefab", prefab);

            visualizerObject.SetActive(true);
            yield return null;

            Assert.That(ReadInt(visualizer, "PoolCountForTests"), Is.EqualTo(16));
            for (var index = 0; index < 64; index++)
            {
                Assert.That(Invoke(visualizer, "AcquireForTests", "front", index), Is.Not.Null, "pointer " + index);
            }

            Assert.That(Invoke(visualizer, "AcquireForTests", "front", 64), Is.Null);
            Assert.That(ReadInt(visualizer, "TotalCreatedForTests"), Is.EqualTo(64));
        }

        [Test]
        public void ImportedSampleParticleVisualizer_ReleasesOnUpZeroFrameTimeoutAndDisable()
        {
            var visualizerType = FindImportedSampleType("Blaze.Radar.Samples.RadarWorldPointerVisualizer");
            var visualizerObject = new GameObject("RadarWorldPointerVisualizerReleaseTests");
            createdObjects.Add(visualizerObject);
            var visualizer = visualizerObject.AddComponent(visualizerType);
            var prefabObject = new GameObject("ParticlePrototype");
            createdObjects.Add(prefabObject);
            var prefab = prefabObject.AddComponent<ParticleSystem>();
            Invoke(visualizer, "ConfigurePoolForTests", prefab, 3, 8);
            var screen = Screen("front", 4096, 1536);

            Invoke(visualizer, "AcquireForTests", "front", 1);
            var up = Pointer(10f, 20f);
            up.pointerId = 1;
            up.phase = RadarPointerPhase.Up;
            Invoke(visualizer, "ProcessPointer", screen, up);
            Assert.That(ReadInt(visualizer, "ActiveCountForTests"), Is.Zero, "Up must release immediately.");

            Invoke(visualizer, "AcquireForTests", "front", 2);
            var zeroFrame = Frame(screen, 10L, new List<RadarScreenPointer>());
            Invoke(visualizer, "ProcessFrame", zeroFrame, 0L);
            Invoke(visualizer, "ProcessZeroFrameTimeoutsForTests", Time.unscaledTime + 1f);
            Assert.That(ReadInt(visualizer, "ActiveCountForTests"), Is.Zero, "A sustained zero frame must release.");

            Invoke(visualizer, "AcquireForTests", "front", 3);
            Invoke(visualizer, "OnDisable");
            Assert.That(ReadInt(visualizer, "ActiveCountForTests"), Is.Zero, "Disable must release all effects.");
        }

        [Test]
        public void ImportedSampleParticleAppearance_IsStablePerScreenAndDownIsIntensified()
        {
            var visualizerType = FindImportedSampleType("Blaze.Radar.Samples.RadarWorldPointerVisualizer");
            var lower = (Color)InvokeStatic(visualizerType, "StableScreenColor", "left");
            var upper = (Color)InvokeStatic(visualizerType, "StableScreenColor", "LEFT");
            Assert.That(lower, Is.EqualTo(upper));

            var normalObject = new GameObject("NormalParticle");
            var downObject = new GameObject("DownParticle");
            createdObjects.Add(normalObject);
            createdObjects.Add(downObject);
            var normal = normalObject.AddComponent<ParticleSystem>();
            var down = downObject.AddComponent<ParticleSystem>();
            InvokeStatic(visualizerType, "ApplyAppearance", normal, "front", false);
            InvokeStatic(visualizerType, "ApplyAppearance", down, "front", true);

            Assert.That(down.main.startSize.constant, Is.GreaterThan(normal.main.startSize.constant));
            Assert.That(down.main.startLifetime.constant, Is.GreaterThan(normal.main.startLifetime.constant));
            Assert.That(down.emission.rateOverTime.constant, Is.GreaterThan(normal.emission.rateOverTime.constant));
        }

        [Test]
        public void ImportedSamplePresenter_ZeroFrameClearsTrackedPointersAndLiveTable()
        {
            var presenterType = FindImportedSampleType("Blaze.Radar.Samples.MultiScreenCameraRoutingPresenter");
            var panelType = FindImportedSampleType("Blaze.Radar.Samples.RadarSampleLogPanel");
            var presenterObject = new GameObject("MultiScreenCameraRoutingPresenterZeroFrameTests");
            var panelObject = new GameObject("RadarSampleLogPanelZeroFrameTests");
            createdObjects.Add(presenterObject);
            createdObjects.Add(panelObject);
            var presenter = presenterObject.AddComponent(presenterType);
            var panel = panelObject.AddComponent(panelType);
            var liveText = CreateText("LeftLivePointers");
            SetField(panel, "leftPointsText", liveText);
            SetField(presenter, "logPanel", panel);
            var screen = Screen("left", 1920, 1440);
            var down = Pointer(321f, 654f);
            down.pointerId = 7;
            down.phase = RadarPointerPhase.Down;

            Invoke(panel, "RecordMiss", screen, down, Vector2.zero, default(Ray), "test");
            Invoke(presenter, "HandleScreenFrame", Frame(screen, 1L, new List<RadarScreenPointer> { down }), 0L);
            Invoke(panel, "LateUpdate");
            Assert.That(ReadCollectionCount(presenter, "activePointers"), Is.EqualTo(1));
            StringAssert.Contains("P7", liveText.text);

            Invoke(presenter, "HandleScreenFrame", Frame(screen, 2L, new List<RadarScreenPointer>()), 0L);
            Invoke(panel, "LateUpdate");
            Assert.That(ReadCollectionCount(presenter, "activePointers"), Is.Zero);
            StringAssert.Contains("No active pointers", liveText.text);
            StringAssert.DoesNotContain("P7", liveText.text);
        }

        [UnityTest]
        public IEnumerator ImportedSampleLogPanel_AgeRefreshesWithoutNewFrames()
        {
            var panelType = FindImportedSampleType("Blaze.Radar.Samples.RadarSampleLogPanel");
            var panelObject = new GameObject("RadarSampleLogPanelAgeTests");
            createdObjects.Add(panelObject);
            var panel = panelObject.AddComponent(panelType);
            var summaryText = CreateText("FrameSummary");
            SetField(panel, "frameSummaryText", summaryText);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Invoke(panel, "RecordFrame", Frame(Screen("front", 4096, 1536), 5L, new List<RadarScreenPointer>(), timestamp), 0L);
            var initialAge = ParseAge(summaryText.text);

            yield return new WaitForSecondsRealtime(0.15f);
            Invoke(panel, "LateUpdate");

            Assert.That(ParseAge(summaryText.text), Is.GreaterThanOrEqualTo(initialAge + 80L));
        }

        [Test]
        public void ImportedSampleLogPanel_HistoryHasHardThreeHundredLineCap()
        {
            var panelType = FindImportedSampleType("Blaze.Radar.Samples.RadarSampleLogPanel");
            var panelObject = new GameObject("RadarSampleLogPanelCapTests");
            createdObjects.Add(panelObject);
            var panel = panelObject.AddComponent(panelType);

            for (var index = 0; index < 305; index++)
            {
                Invoke(panel, "RecordLifecycle", "line " + index);
            }

            Assert.That(ReadInt(panel, "LineCount"), Is.EqualTo(300));
        }

        [UnityTest]
        public IEnumerator ImportedSampleLogPanel_OnlyMoveIsThrottledWhileLiveTableStaysCurrent()
        {
            var panelType = FindImportedSampleType("Blaze.Radar.Samples.RadarSampleLogPanel");
            var panelObject = new GameObject("RadarSampleLogPanelThrottleTests");
            createdObjects.Add(panelObject);
            var panel = panelObject.AddComponent(panelType);
            var liveText = CreateText("FrontLivePointers");
            SetField(panel, "frontPointsText", liveText);
            var screen = Screen("front", 4096, 1536);
            var move = Pointer(100f, 200f);
            move.pointerId = 4;
            move.phase = RadarPointerPhase.Move;

            Invoke(panel, "RecordMiss", screen, move, Vector2.zero, default(Ray), "first move");
            var afterFirstMove = ReadInt(panel, "LineCount");
            move.pixelX = 777f;
            Invoke(panel, "RecordMiss", screen, move, Vector2.zero, default(Ray), "latest move");
            Invoke(panel, "LateUpdate");
            Assert.That(ReadInt(panel, "LineCount"), Is.EqualTo(afterFirstMove), "Immediate Move should be throttled.");
            StringAssert.Contains("777 x 200", liveText.text, "Live data must not be throttled.");

            yield return new WaitForSecondsRealtime(0.11f);
            Invoke(panel, "RecordMiss", screen, move, Vector2.zero, default(Ray), "next move");
            Assert.That(ReadInt(panel, "LineCount"), Is.EqualTo(afterFirstMove + 1), "Move should log again at 10 Hz.");

            move.phase = RadarPointerPhase.Down;
            Invoke(panel, "RecordMiss", screen, move, Vector2.zero, default(Ray), "down");
            Assert.That(ReadInt(panel, "LineCount"), Is.EqualTo(afterFirstMove + 2), "Down must never be Move-throttled.");
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

        private Text CreateText(string name)
        {
            var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            createdObjects.Add(gameObject);
            return gameObject.GetComponent<Text>();
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

        private static RadarScreenPointerFrame Frame(
            RadarScreenInfo screen,
            long sequence,
            List<RadarScreenPointer> pointers,
            long? timestamp = null)
        {
            return new RadarScreenPointerFrame
            {
                screen = screen,
                sequence = sequence,
                timestampUnixMilliseconds = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                pointers = pointers
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
            var sampleAssemblyLoaded = false;
            for (var index = 0; index < assemblies.Length; index++)
            {
                if (string.Equals(
                    assemblies[index].GetName().Name,
                    "Blaze.Radar.Sample.MultiScreenCameraRouting",
                    StringComparison.Ordinal))
                {
                    sampleAssemblyLoaded = true;
                }

                var type = assemblies[index].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            if (sampleAssemblyLoaded)
            {
                Assert.Fail("The imported sample assembly is missing required type '" + fullName + "'.");
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

        private static object InvokeStatic(Type type, string method, params object[] arguments)
        {
            var info = type.GetMethod(
                method,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, method);
            return info.Invoke(null, arguments);
        }

        private static void SetField(object target, string field, object value)
        {
            var info = target.GetType().GetField(
                field,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, field);
            info.SetValue(target, value);
        }

        private static int ReadCollectionCount(object target, string field)
        {
            var fieldInfo = target.GetType().GetField(
                field,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fieldInfo, Is.Not.Null, field);
            var collection = fieldInfo.GetValue(target);
            Assert.That(collection, Is.Not.Null, field);
            var count = collection.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(count, Is.Not.Null, field + ".Count");
            return (int)count.GetValue(collection, null);
        }

        private static int ReadInt(object target, string property)
        {
            var info = target.GetType().GetProperty(
                property,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, property);
            return (int)info.GetValue(target, null);
        }

        private static bool ReadBool(object target, string field)
        {
            var info = target.GetType().GetField(
                field,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, field);
            return (bool)info.GetValue(target);
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

        private static void AssertPointerIds(
            RadarPointerBatchPayload batch,
            string screenId,
            params int[] pointerIds)
        {
            var frame = FindFrame(batch, screenId);
            Assert.That(frame.pointers, Has.Count.EqualTo(pointerIds.Length), screenId);
            for (var index = 0; index < pointerIds.Length; index++)
            {
                Assert.That(frame.pointers[index].pointerId, Is.EqualTo(pointerIds[index]), screenId + " pointer " + index);
            }
        }

        private static long ParseAge(string summary)
        {
            var match = Regex.Match(summary ?? string.Empty, @"AGE (\d+) ms");
            Assert.That(match.Success, Is.True, summary);
            return long.Parse(match.Groups[1].Value);
        }

        private static void AssertSerializedScreen(
            string settings,
            string screenId,
            int width,
            int height,
            bool primary,
            int order)
        {
            var pattern = @"(?ms)- screenId: " + Regex.Escape(screenId) +
                @"\s+displayName: .*?\s+defaultWidthPixels: " + width +
                @"\s+defaultHeightPixels: " + height +
                @"\s+enabled: 1\s+isPrimary: " + (primary ? 1 : 0) +
                @"\s+order: " + order;
            Assert.That(Regex.IsMatch(settings, pattern), Is.True, screenId + " settings contract");
        }

        private static string NormalizeNewlines(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n");
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
