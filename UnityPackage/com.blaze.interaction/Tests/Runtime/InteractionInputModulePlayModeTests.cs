using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Blaze.Interaction.Tests
{
    public sealed class InteractionInputModulePlayModeTests
    {
        private GameObject eventSystemObject;
        private GameObject canvasObject;
        private InteractionInputModule module;
        private readonly List<GameObject> extraObjects = new List<GameObject>();
        private readonly List<BaseRaycaster> disabledRaycasters = new List<BaseRaycaster>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            eventSystemObject = new GameObject(
                "EventSystem",
                typeof(EventSystem),
                typeof(InteractionInputModule));
            module = eventSystemObject.GetComponent<InteractionInputModule>();
            module.InputMode = InteractionInputMode.InteractionOnly;
            module.SurfaceId = "main";

            canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(GraphicRaycaster));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (var index = 0; index < disabledRaycasters.Count; index++)
            {
                if (disabledRaycasters[index] != null)
                {
                    disabledRaycasters[index].enabled = true;
                }
            }

            for (var index = 0; index < extraObjects.Count; index++)
            {
                if (extraObjects[index] != null)
                {
                    Object.Destroy(extraObjects[index]);
                }
            }

            Object.Destroy(canvasObject);
            Object.Destroy(eventSystemObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator DownAndUp_ClicksButton()
        {
            var clicks = 0;
            CreateButton("Center", new Vector2(.5f, .5f)).onClick.AddListener(() => clicks++);
            yield return null;

            ProcessFrame(Point(1, .5f, .5f, InteractionPhase.Down));
            ProcessFrame(Point(1, .5f, .5f, InteractionPhase.Up));

            Assert.That(clicks, Is.EqualTo(1));
            Assert.That(module.ActivePointers, Is.Empty);
        }

        [UnityTest]
        public IEnumerator TwoPointers_ClickIndependentButtons()
        {
            var leftClicks = 0;
            var rightClicks = 0;
            CreateButton("Left", new Vector2(.25f, .5f)).onClick.AddListener(() => leftClicks++);
            CreateButton("Right", new Vector2(.75f, .5f)).onClick.AddListener(() => rightClicks++);
            yield return null;

            ProcessFrame(
                Point(10, .25f, .5f, InteractionPhase.Down),
                Point(20, .75f, .5f, InteractionPhase.Down));
            ProcessFrame(
                Point(10, .25f, .5f, InteractionPhase.Up),
                Point(20, .75f, .5f, InteractionPhase.Up));

            Assert.That(leftClicks, Is.EqualTo(1));
            Assert.That(rightClicks, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CancelAndDeactivate_ReleaseWithoutSynthesizingClick()
        {
            var clicks = 0;
            CreateButton("Center", new Vector2(.5f, .5f)).onClick.AddListener(() => clicks++);
            yield return null;

            ProcessFrame(Point(1, .5f, .5f, InteractionPhase.Down));
            ProcessFrame(Point(1, .5f, .5f, InteractionPhase.Cancel));
            Assert.That(module.ActivePointers, Is.Empty);

            ProcessFrame(Point(2, .5f, .5f, InteractionPhase.Down));
            module.DeactivateModule();

            Assert.That(module.ActivePointers, Is.Empty);
            Assert.That(clicks, Is.Zero);
        }

        [UnityTest]
        public IEnumerator SurfaceFilter_CancelsOldPressAndRoutesOnlySelectedSurface()
        {
            var clicks = 0;
            CreateButton("Center", new Vector2(.5f, .5f)).onClick.AddListener(() => clicks++);
            module.SurfaceId = "front";
            yield return null;

            ProcessFrame("left", Point(1, .5f, .5f, InteractionPhase.Down, "left"));
            Assert.That(module.ActivePointers, Is.Empty);
            ProcessFrame("front", Point(2, .5f, .5f, InteractionPhase.Down, "front"));
            Assert.That(module.ActivePointers, Has.Count.EqualTo(1));

            module.SurfaceId = "right";
            Assert.That(module.ActivePointers, Is.Empty);
            ProcessFrame("right", Point(3, .5f, .5f, InteractionPhase.Down, "right"));
            ProcessFrame("right", Point(3, .5f, .5f, InteractionPhase.Up, "right"));

            Assert.That(clicks, Is.EqualTo(1));
        }

        [Test]
        public void HighRateMoves_AreCoalescedWithoutDroppingLifecycleEdges()
        {
            module.InjectFrame(Frame("main", Point(7, .5f, .5f, InteractionPhase.Down)));
            for (var index = 0; index < 500; index++)
            {
                module.InjectFrame(Frame(
                    "main",
                    Point(7, .5f + index / 2000f, .5f, InteractionPhase.Move)));
            }

            module.InjectFrame(Frame("main", Point(7, .75f, .5f, InteractionPhase.Up)));
            module.InjectFrame(Frame("main"));

            Assert.That(module.PendingFrameCount, Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator InteractionAndMouseDebug_KeepsNativeMousePointerAvailable()
        {
            module.InputMode = InteractionInputMode.InteractionAndMouseDebug;
            yield return null;

            module.Process();

            Assert.That(module.ActivePointers.ContainsKey(-1), Is.True);
        }

        [UnityTest]
        public IEnumerator PrimarySurface_DrivesButtonToggleSliderAndDragInOrder()
        {
            var events = new List<string>();
            CreateButton("Button", new Vector2(.5f, .82f)).onClick.AddListener(() => events.Add("button"));
            CreateToggle("Toggle", new Vector2(.5f, .64f)).onValueChanged.AddListener(_ => events.Add("toggle"));
            CreateSlider("Slider", new Vector2(.5f, .46f)).onValueChanged.AddListener(_ => events.Add("slider"));
            var dragTarget = CreateUiTarget("Drag", new Vector2(.5f, .23f), new Vector2(320f, 120f));
            dragTarget.AddComponent<DragRecorder>().Events = events;
            yield return null;

            Click(10, .5f, .82f);
            Click(11, .5f, .64f);
            ProcessFrame(Point(12, .43f, .46f, InteractionPhase.Down));
            ProcessFrame(Point(12, .57f, .46f, InteractionPhase.Move));
            ProcessFrame(Point(12, .57f, .46f, InteractionPhase.Up));
            ProcessFrame(Point(13, .46f, .23f, InteractionPhase.Down));
            ProcessFrame(Point(13, .54f, .23f, InteractionPhase.Move));
            ProcessFrame(Point(13, .54f, .23f, InteractionPhase.Up));

            AssertInOrder(events, "button", "toggle", "slider", "drag.begin", "drag.end");
        }

        [UnityTest]
        public IEnumerator PrimarySurface_DrivesPhysics3DAnd2DPointerPaths()
        {
            DisableSceneRaycasters();
            var events = new List<string>();
            CreatePhysics3DTarget(new Vector2(.38f, .55f), events);
            CreatePhysics2DTarget(new Vector2(.62f, .45f), events);
            Physics.SyncTransforms();
            Physics2D.SyncTransforms();
            yield return null;

            Click(21, .38f, .55f);
            Click(22, .62f, .45f);

            CollectionAssert.Contains(events, "physics3d");
            CollectionAssert.Contains(events, "physics2d");
        }

        private void Click(long id, float x, float y)
        {
            ProcessFrame(Point(id, x, y, InteractionPhase.Down));
            ProcessFrame(Point(id, x, y, InteractionPhase.Up));
        }

        private void ProcessFrame(params InteractionPoint[] points)
        {
            ProcessFrame("main", points);
        }

        private void ProcessFrame(string surfaceId, params InteractionPoint[] points)
        {
            module.InjectFrame(Frame(surfaceId, points));
            module.Process();
        }

        private static InteractionFrame Frame(string surfaceId, params InteractionPoint[] points)
        {
            return new InteractionFrame
            {
                ProviderId = "blaze.test",
                ProviderInstanceId = "test-main",
                SurfaceId = surfaceId,
                Sequence = 1,
                TimestampUnixMs = 1,
                Points = new List<InteractionPoint>(points)
            };
        }

        private static InteractionPoint Point(
            long id,
            float x,
            float y,
            InteractionPhase phase,
            string surfaceId = "main")
        {
            return new InteractionPoint
            {
                Id = id,
                SurfaceId = surfaceId,
                ProviderId = "blaze.test",
                ProviderInstanceId = "test-main",
                SourceId = "source",
                Phase = phase,
                NormalizedPosition = new Vector2Data { X = x, Y = y },
                PixelPosition = new Vector2Data { X = x * 1920f, Y = y * 1080f },
                Confidence = 1f,
                TimestampUnixMs = 1
            };
        }

        private Button CreateButton(string name, Vector2 position)
        {
            return CreateUiTarget(name, position, new Vector2(180f, 90f)).AddComponent<Button>();
        }

        private Toggle CreateToggle(string name, Vector2 position)
        {
            return CreateUiTarget(name, position, new Vector2(220f, 90f)).AddComponent<Toggle>();
        }

        private Slider CreateSlider(string name, Vector2 position)
        {
            var root = CreateUiTarget(name, position, new Vector2(360f, 90f));
            var fill = CreateSliderChild("Fill", root.transform);
            var handle = CreateSliderChild("Handle", root.transform);
            var handleRect = handle.GetComponent<RectTransform>();
            handleRect.anchorMin = new Vector2(.5f, .5f);
            handleRect.anchorMax = new Vector2(.5f, .5f);
            handleRect.sizeDelta = new Vector2(24f, 24f);
            var slider = root.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = .25f;
            slider.fillRect = fill.GetComponent<RectTransform>();
            slider.handleRect = handleRect;
            slider.targetGraphic = handle.GetComponent<Image>();
            return slider;
        }

        private static GameObject CreateSliderChild(string name, Transform parent)
        {
            var child = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            child.transform.SetParent(parent, false);
            var rect = child.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return child;
        }

        private GameObject CreateUiTarget(string name, Vector2 position, Vector2 size)
        {
            var value = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            value.transform.SetParent(canvasObject.transform, false);
            var rect = value.GetComponent<RectTransform>();
            rect.anchorMin = position;
            rect.anchorMax = position;
            rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = size;
            return value;
        }

        private void CreatePhysics3DTarget(Vector2 position, List<string> events)
        {
            var camera = CreateEventCamera(typeof(PhysicsRaycaster));
            var target = new GameObject("Physics3D", typeof(BoxCollider), typeof(ClickRecorder));
            target.transform.position = ScreenPointAtDepth(camera, position, 10f);
            target.GetComponent<BoxCollider>().size = new Vector3(2f, 2f, 1f);
            target.GetComponent<ClickRecorder>().Configure(events, "physics3d");
            extraObjects.Add(target);
        }

        private void CreatePhysics2DTarget(Vector2 position, List<string> events)
        {
            var camera = CreateEventCamera(typeof(Physics2DRaycaster));
            var target = new GameObject("Physics2D", typeof(BoxCollider2D), typeof(ClickRecorder));
            target.transform.position = ScreenPointAtDepth(camera, position, 10f);
            target.GetComponent<BoxCollider2D>().size = new Vector2(2f, 2f);
            target.GetComponent<ClickRecorder>().Configure(events, "physics2d");
            extraObjects.Add(target);
        }

        private Camera CreateEventCamera(Type raycasterType)
        {
            var value = new GameObject("Event Camera", typeof(Camera), raycasterType);
            var camera = value.GetComponent<Camera>();
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.orthographic = true;
            camera.orthographicSize = 5f;
            camera.clearFlags = CameraClearFlags.Nothing;
            extraObjects.Add(value);
            return camera;
        }

        private void DisableSceneRaycasters()
        {
            var raycasters = Object.FindObjectsOfType<BaseRaycaster>();
            for (var index = 0; index < raycasters.Length; index++)
            {
                if (raycasters[index].enabled)
                {
                    raycasters[index].enabled = false;
                    disabledRaycasters.Add(raycasters[index]);
                }
            }
        }

        private static Vector3 ScreenPointAtDepth(Camera camera, Vector2 position, float depth)
        {
            return camera.ScreenToWorldPoint(new Vector3(
                position.x * Screen.width,
                position.y * Screen.height,
                depth));
        }

        private static void AssertInOrder(IReadOnlyList<string> actual, params string[] expected)
        {
            var next = 0;
            for (var index = 0; index < actual.Count && next < expected.Length; index++)
            {
                if (actual[index] == expected[next])
                {
                    next++;
                }
            }

            Assert.That(next, Is.EqualTo(expected.Length), string.Join(",", actual));
        }

        private sealed class DragRecorder : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            public List<string> Events { get; set; }
            public void OnBeginDrag(PointerEventData eventData) { Events.Add("drag.begin"); }
            public void OnDrag(PointerEventData eventData) { Events.Add("drag.move"); }
            public void OnEndDrag(PointerEventData eventData) { Events.Add("drag.end"); }
        }

        private sealed class ClickRecorder : MonoBehaviour, IPointerClickHandler
        {
            private List<string> events;
            private string marker;
            public void Configure(List<string> values, string value) { events = values; marker = value; }
            public void OnPointerClick(PointerEventData eventData) { events.Add(marker); }
        }
    }
}
