using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Blaze.Radar.Tests
{
    public sealed class RadarInputModulePlayModeTests
    {
        private GameObject _eventSystemObject;
        private GameObject _canvasObject;
        private GameObject _dispatcherObject;
        private RadarRuntimeSettings _runtimeSettings;
        private RadarInputModule _module;
        private readonly List<GameObject> _extraObjects = new List<GameObject>();
        private readonly List<BaseRaycaster> _disabledSceneRaycasters = new List<BaseRaycaster>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(RadarInputModule));
            _module = _eventSystemObject.GetComponent<RadarInputModule>();
            _module.InputMode = RadarInputMode.RadarOnly;

            _canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(GraphicRaycaster));
            _canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (var index = 0; index < _extraObjects.Count; index++)
            {
                if (_extraObjects[index] == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    Object.Destroy(_extraObjects[index]);
                }
                else
                {
                    Object.DestroyImmediate(_extraObjects[index]);
                }
            }

            _extraObjects.Clear();
            for (var index = 0; index < _disabledSceneRaycasters.Count; index++)
            {
                if (_disabledSceneRaycasters[index] != null)
                {
                    _disabledSceneRaycasters[index].enabled = true;
                }
            }

            _disabledSceneRaycasters.Clear();
            if (Application.isPlaying)
            {
                Object.Destroy(_canvasObject);
                Object.Destroy(_eventSystemObject);
                Object.Destroy(_dispatcherObject);
                Object.Destroy(_runtimeSettings);
            }
            else
            {
                Object.DestroyImmediate(_canvasObject);
                Object.DestroyImmediate(_eventSystemObject);
                Object.DestroyImmediate(_dispatcherObject);
                Object.DestroyImmediate(_runtimeSettings);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator DownAndUp_ClicksButton()
        {
            var clicks = 0;
            var button = CreateButton("Center", new Vector2(0.5f, 0.5f));
            button.onClick.AddListener(() => clicks++);
            yield return null;

            ProcessFrame(Pointer(1, 0.5f, 0.5f, RadarPointerPhase.Down));
            ProcessFrame(Pointer(1, 0.5f, 0.5f, RadarPointerPhase.Up));

            Assert.That(clicks, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TwoPointers_ClickIndependentButtons()
        {
            var leftClicks = 0;
            var rightClicks = 0;
            CreateButton("Left", new Vector2(0.25f, 0.5f)).onClick.AddListener(() => leftClicks++);
            CreateButton("Right", new Vector2(0.75f, 0.5f)).onClick.AddListener(() => rightClicks++);
            yield return null;

            ProcessFrame(
                Pointer(10, 0.25f, 0.5f, RadarPointerPhase.Down),
                Pointer(20, 0.75f, 0.5f, RadarPointerPhase.Down));
            ProcessFrame(
                Pointer(10, 0.25f, 0.5f, RadarPointerPhase.Up),
                Pointer(20, 0.75f, 0.5f, RadarPointerPhase.Up));

            Assert.That(leftClicks, Is.EqualTo(1));
            Assert.That(rightClicks, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ConnectionLoss_CancelsPressedPointersWithoutClicking()
        {
            var clicks = 0;
            var button = CreateButton("Center", new Vector2(0.5f, 0.5f));
            button.onClick.AddListener(() => clicks++);
            yield return null;

            ProcessFrame(Pointer(1, 0.5f, 0.5f, RadarPointerPhase.Down));
            Assert.That(_module.ActivePointers.Count, Is.EqualTo(1));

            var cancelMethod = typeof(RadarInputModule).GetMethod(
                "CancelAllPointers",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(cancelMethod, Is.Not.Null, "RadarInputModule must expose a safe disconnect reset.");
            cancelMethod.Invoke(_module, null);

            Assert.That(_module.ActivePointers, Is.Empty);
            Assert.That(clicks, Is.Zero, "Connection loss must not synthesize a click.");
        }

        [UnityTest]
        public IEnumerator IsPointerOverGameObject_UsesTheEventSystemRaycastResult()
        {
            CreateButton("Center", new Vector2(0.5f, 0.5f));
            yield return null;

            ProcessFrame(Pointer(42, 0.5f, 0.5f, RadarPointerPhase.Hover));

            Assert.That(_module.IsPointerOverGameObject(42), Is.True);
            Assert.That(_module.IsPointerOverGameObject(999), Is.False);
        }

        [UnityTest]
        public IEnumerator DeactivateModule_CancelsPointersWithoutSynthesizingClick()
        {
            var clicks = 0;
            var button = CreateButton("Center", new Vector2(0.5f, 0.5f));
            button.onClick.AddListener(() => clicks++);
            yield return null;

            ProcessFrame(Pointer(7, 0.5f, 0.5f, RadarPointerPhase.Down));
            _module.DeactivateModule();

            Assert.That(_module.ActivePointers, Is.Empty);
            Assert.That(clicks, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PointerCancellation_IsSafeWhenAHandlerCancelsAgain()
        {
            var button = CreateButton("Center", new Vector2(0.5f, 0.5f));
            var reentrantHandler = button.gameObject.AddComponent<CancelOnPointerUp>();
            reentrantHandler.Module = _module;
            yield return null;

            ProcessFrame(Pointer(9, 0.5f, 0.5f, RadarPointerPhase.Down));

            Assert.DoesNotThrow(_module.CancelAllPointers);
            Assert.That(_module.ActivePointers, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ScreenFilter_IgnoresOtherScreenAndClicksSelectedScreenButton()
        {
            var clicks = 0;
            var button = CreateButton("Center", new Vector2(0.5f, 0.5f));
            button.onClick.AddListener(() => clicks++);
            _module.ScreenId = "front";
            yield return null;

            ProcessScreenFrame(ScreenFrame("left", false, ScreenPointer(1, 0.5f, 0.5f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("left", false, ScreenPointer(1, 0.5f, 0.5f, RadarPointerPhase.Up)));
            ProcessScreenFrame(ScreenFrame("front", true, ScreenPointer(2, 0.5f, 0.5f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("front", true, ScreenPointer(2, 0.5f, 0.5f, RadarPointerPhase.Up)));

            Assert.That(clicks, Is.EqualTo(1));
            Assert.That(_module.ActivePointers, Is.Empty);
        }

        [UnityTest]
        public IEnumerator EmptyScreenId_AcceptsOnlyPrimaryScreenFrames()
        {
            var clicks = 0;
            var button = CreateButton("Center", new Vector2(0.5f, 0.5f));
            button.onClick.AddListener(() => clicks++);
            _module.ScreenId = string.Empty;
            yield return null;

            ProcessScreenFrame(ScreenFrame("left", false, ScreenPointer(1, 0.5f, 0.5f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("left", false, ScreenPointer(1, 0.5f, 0.5f, RadarPointerPhase.Up)));
            ProcessScreenFrame(ScreenFrame("front", true, ScreenPointer(2, 0.5f, 0.5f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("front", true, ScreenPointer(2, 0.5f, 0.5f, RadarPointerPhase.Up)));

            Assert.That(clicks, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ChangingScreenId_CancelsOldPressAndAcceptsNewScreen()
        {
            var clicks = 0;
            var button = CreateButton("Center", new Vector2(0.5f, 0.5f));
            button.onClick.AddListener(() => clicks++);
            _module.ScreenId = "front";
            yield return null;

            ProcessScreenFrame(ScreenFrame("front", true, ScreenPointer(7, 0.5f, 0.5f, RadarPointerPhase.Down)));
            Assert.That(_module.ActivePointers.Count, Is.EqualTo(1));

            _module.ScreenId = "right";

            Assert.That(_module.ActivePointers, Is.Empty);
            Assert.That(clicks, Is.Zero);

            ProcessScreenFrame(ScreenFrame("right", false, ScreenPointer(7, 0.5f, 0.5f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("right", false, ScreenPointer(7, 0.5f, 0.5f, RadarPointerPhase.Up)));

            Assert.That(clicks, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator MalformedAndOtherScreenFrames_DoNotDisturbSelectedPointer()
        {
            var clicks = 0;
            var button = CreateButton("Center", new Vector2(0.5f, 0.5f));
            button.onClick.AddListener(() => clicks++);
            _module.ScreenId = "front";
            yield return null;

            ProcessScreenFrame(ScreenFrame("front", true, ScreenPointer(9, 0.5f, 0.5f, RadarPointerPhase.Down)));
            Assert.That(_module.ActivePointers.Count, Is.EqualTo(1));

            ProcessScreenFrame(null);
            ProcessScreenFrame(new RadarScreenPointerFrame { screen = null });
            ProcessScreenFrame(ScreenFrame("left", false, ScreenPointer(9, 0.5f, 0.5f, RadarPointerPhase.Up)));

            Assert.That(_module.ActivePointers.Count, Is.EqualTo(1));
            Assert.That(clicks, Is.Zero);

            ProcessScreenFrame(ScreenFrame("front", true, ScreenPointer(9, 0.5f, 0.5f, RadarPointerPhase.Up)));

            Assert.That(_module.ActivePointers, Is.Empty);
            Assert.That(clicks, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DispatcherDisconnectWithCachedDown_DoesNotReactivatePointer()
        {
            var fixture = ConfigureDispatcher();
            fixture.Client.SetConnected(true);
            fixture.Dispatcher.TickForTests();
            yield return null;

            fixture.Client.Publish(Batch(
                ScreenFrame("front", true, ScreenPointer(41, 0.5f, 0.5f, RadarPointerPhase.Down))));
            fixture.Client.SetConnected(false);

            fixture.Dispatcher.TickForTests();
            _module.Process();

            Assert.That(fixture.Client.IsConnected, Is.False);
            Assert.That(_module.ActivePointers, Is.Empty,
                "A batch cached before disconnect must not recreate a pressed pointer after cancellation.");
        }

        [UnityTest]
        public IEnumerator DispatcherConnectionLifecycle_CancelsAndResumesProductionSubscription()
        {
            var clicks = 0;
            var button = CreateButton("Center", new Vector2(0.5f, 0.5f));
            button.onClick.AddListener(() => clicks++);
            var fixture = ConfigureDispatcher();
            fixture.Client.SetConnected(true);
            fixture.Dispatcher.TickForTests();
            yield return null;

            fixture.Client.Publish(Batch(
                ScreenFrame("front", true, ScreenPointer(51, 0.5f, 0.5f, RadarPointerPhase.Down))));
            fixture.Dispatcher.TickForTests();
            _module.Process();
            Assert.That(_module.ActivePointers.Count, Is.EqualTo(1));

            fixture.Client.SetConnected(false);
            fixture.Dispatcher.TickForTests();
            Assert.That(_module.ActivePointers, Is.Empty);
            Assert.That(clicks, Is.Zero);

            fixture.Client.SetConnected(true);
            fixture.Dispatcher.TickForTests();
            fixture.Client.Publish(Batch(
                ScreenFrame("front", true, ScreenPointer(52, 0.5f, 0.5f, RadarPointerPhase.Down))));
            fixture.Dispatcher.TickForTests();
            _module.Process();
            fixture.Client.Publish(Batch(
                ScreenFrame("front", true, ScreenPointer(52, 0.5f, 0.5f, RadarPointerPhase.Up))));
            fixture.Dispatcher.TickForTests();
            _module.Process();

            Assert.That(clicks, Is.EqualTo(1));
            Assert.That(_module.ActivePointers, Is.Empty);
        }

        [UnityTest]
        public IEnumerator RadarAndMouseDebug_KeepsNativeMousePointerAvailable()
        {
            _module.InputMode = RadarInputMode.RadarAndMouseDebug;
            yield return null;

            _module.Process();

            Assert.That(_module.ActivePointers.ContainsKey(-1), Is.True,
                "RadarAndMouseDebug must continue to process Unity's native mouse as pointer -1.");
        }

        [UnityTest]
        public IEnumerator PrimaryScreenFrame_DrivesButtonToggleSliderAndDragInOrder()
        {
            var events = new List<string>();
            var button = CreateButton("Basic Button", new Vector2(0.5f, 0.82f));
            button.onClick.AddListener(() => events.Add("button.click"));

            var toggle = CreateToggle("Basic Toggle", new Vector2(0.5f, 0.64f));
            toggle.onValueChanged.AddListener(_ => events.Add("toggle.changed"));

            var slider = CreateSlider("Basic Slider", new Vector2(0.5f, 0.46f));
            slider.onValueChanged.AddListener(_ => events.Add("slider.changed"));

            var dragTarget = CreateUiTarget("Basic Drag", new Vector2(0.5f, 0.23f), new Vector2(320f, 120f));
            var dragRecorder = dragTarget.AddComponent<DragPathRecorder>();
            dragRecorder.Events = events;
            yield return null;

            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(10, 0.5f, 0.82f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(10, 0.5f, 0.82f, RadarPointerPhase.Up)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(11, 0.5f, 0.64f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(11, 0.5f, 0.64f, RadarPointerPhase.Up)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(12, 0.43f, 0.46f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(12, 0.57f, 0.46f, RadarPointerPhase.Move)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(12, 0.57f, 0.46f, RadarPointerPhase.Up)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(13, 0.46f, 0.23f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(13, 0.54f, 0.23f, RadarPointerPhase.Move)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(13, 0.54f, 0.23f, RadarPointerPhase.Up)));

            AssertEventsInOrder(
                events,
                "button.click",
                "toggle.changed",
                "slider.changed",
                "drag.begin",
                "drag.end");
        }

        [UnityTest]
        public IEnumerator PrimaryScreenFrame_DrivesPhysics3DPointerPath()
        {
            DisableSceneRaycasters();
            var events = new List<string>();
            CreatePhysics3DTarget(new Vector2(0.38f, 0.55f), events);
            Physics.SyncTransforms();
            yield return null;

            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(21, 0.38f, 0.55f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(21, 0.38f, 0.55f, RadarPointerPhase.Up)));

            CollectionAssert.Contains(events, "physics3d.click");
        }

        [UnityTest]
        public IEnumerator PrimaryScreenFrame_DrivesPhysics2DPointerPath()
        {
            DisableSceneRaycasters();
            var events = new List<string>();
            CreatePhysics2DTarget(new Vector2(0.62f, 0.45f), events);
            Physics2D.SyncTransforms();
            yield return null;

            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(22, 0.62f, 0.45f, RadarPointerPhase.Down)));
            ProcessScreenFrame(ScreenFrame("main", true, ScreenPointer(22, 0.62f, 0.45f, RadarPointerPhase.Up)));

            CollectionAssert.Contains(events, "physics2d.click");
        }

        [Test]
        public void BasicInteractionLogger_EnforcesHardHistoryCaps()
        {
            var logger = CreateBasicInteractionLogger(out var loggerType);
            SetPrivateField(logger, loggerType, "maxFrameLogLines", 500);
            SetPrivateField(logger, loggerType, "maxEventSystemLogLines", 500);

            var appendFrame = RequirePrivateMethod(loggerType, "AppendFrameLog");
            var appendEvent = RequirePrivateMethod(loggerType, "AppendEventLog");
            for (var index = 0; index < 250; index++)
            {
                appendFrame.Invoke(logger, new object[] { "frame " + index });
            }

            for (var index = 0; index < 350; index++)
            {
                appendEvent.Invoke(logger, new object[] { "EVENT", "event " + index });
            }

            Assert.That(GetPrivateCollectionCount(logger, loggerType, "_frameEntries"), Is.EqualTo(200));
            Assert.That(GetPrivateCollectionCount(logger, loggerType, "_eventEntries"), Is.EqualTo(300));
        }

        [UnityTest]
        public IEnumerator BasicInteractionLogger_ThrottlesOnlyConsecutiveMoves()
        {
            var logger = CreateBasicInteractionLogger(out var loggerType);
            var liveTextObject = new GameObject(
                "LoggerLiveText",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text));
            _extraObjects.Add(liveTextObject);
            var liveText = liveTextObject.GetComponent<Text>();
            SetPrivateField(logger, loggerType, "latestFrameText", liveText);
            var receiveFrame = RequirePrivateMethod(loggerType, "OnScreenFrameReceived");

            InvokeLoggerFrame(receiveFrame, logger, 1, ScreenPointer(31, 0.1f, 0.1f, RadarPointerPhase.Move));
            InvokeLoggerFrame(receiveFrame, logger, 2, ScreenPointer(31, 0.8f, 0.8f, RadarPointerPhase.Move));
            Assert.That(GetPrivateCollectionCount(logger, loggerType, "_frameEntries"), Is.EqualTo(1));
            Assert.That(
                liveText.text,
                Does.Contain("pixel=(1536.0, 864.0)"),
                "The live position must refresh even while the second consecutive Move history line is throttled.");

            InvokeLoggerFrame(receiveFrame, logger, 3, ScreenPointer(31, 0.2f, 0.2f, RadarPointerPhase.Hover));
            InvokeLoggerFrame(receiveFrame, logger, 4, ScreenPointer(31, 0.3f, 0.3f, RadarPointerPhase.Hover));
            InvokeLoggerFrame(receiveFrame, logger, 5, ScreenPointer(31, 0.4f, 0.4f, RadarPointerPhase.Move));
            InvokeLoggerFrame(receiveFrame, logger, 6, ScreenPointer(31, 0.5f, 0.5f, RadarPointerPhase.Move));
            InvokeLoggerFrame(receiveFrame, logger, 7, ScreenPointer(31, 0.5f, 0.5f, RadarPointerPhase.Down));
            InvokeLoggerFrame(receiveFrame, logger, 8, ScreenPointer(31, 0.6f, 0.6f, RadarPointerPhase.Move));
            InvokeLoggerFrame(receiveFrame, logger, 9, ScreenPointer(31, 0.6f, 0.6f, RadarPointerPhase.Up));
            InvokeLoggerFrame(receiveFrame, logger, 10, ScreenPointer(31, 0.7f, 0.7f, RadarPointerPhase.Move));
            InvokeLoggerFrame(receiveFrame, logger, 11, null);

            Assert.That(GetPrivateCollectionCount(logger, loggerType, "_frameEntries"), Is.EqualTo(8));
            Assert.That(GetPrivateCollectionCount(logger, loggerType, "_eventEntries"), Is.EqualTo(1));

            InvokeLoggerEmptyFrame(receiveFrame, logger, 12);
            InvokeLoggerEmptyFrame(receiveFrame, logger, 13);
            Assert.That(GetPrivateCollectionCount(logger, loggerType, "_frameEntries"), Is.EqualTo(10));

            InvokeLoggerFrame(receiveFrame, logger, 14, ScreenPointer(31, 0.9f, 0.9f, RadarPointerPhase.Move));
            InvokeLoggerFrame(receiveFrame, logger, 15, ScreenPointer(31, 0.95f, 0.95f, RadarPointerPhase.Move));
            Assert.That(GetPrivateCollectionCount(logger, loggerType, "_frameEntries"), Is.EqualTo(11));

            yield return new WaitForSecondsRealtime(0.12f);
            InvokeLoggerFrame(receiveFrame, logger, 16, ScreenPointer(31, 1f, 1f, RadarPointerPhase.Move));
            Assert.That(GetPrivateCollectionCount(logger, loggerType, "_frameEntries"), Is.EqualTo(12));
        }

        private Button CreateButton(string name, Vector2 normalizedPosition)
        {
            return CreateUiTarget(name, normalizedPosition, new Vector2(180f, 90f)).AddComponent<Button>();
        }

        private Toggle CreateToggle(string name, Vector2 normalizedPosition)
        {
            return CreateUiTarget(name, normalizedPosition, new Vector2(220f, 90f)).AddComponent<Toggle>();
        }

        private Slider CreateSlider(string name, Vector2 normalizedPosition)
        {
            var sliderObject = CreateUiTarget(name, normalizedPosition, new Vector2(360f, 90f));
            var fillArea = CreateSliderChild("Fill Area", sliderObject.transform, Vector2.zero);
            var fill = CreateSliderChild("Fill", fillArea.transform, new Vector2(-8f, 0f));
            var handleArea = CreateSliderChild("Handle Slide Area", sliderObject.transform, new Vector2(-24f, 0f));
            var handle = CreateSliderChild("Handle", handleArea.transform, new Vector2(24f, 24f));
            var handleRect = handle.GetComponent<RectTransform>();
            handleRect.anchorMin = new Vector2(0.5f, 0.5f);
            handleRect.anchorMax = new Vector2(0.5f, 0.5f);
            handleRect.sizeDelta = new Vector2(24f, 24f);

            var slider = sliderObject.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0.25f;
            slider.direction = Slider.Direction.LeftToRight;
            slider.fillRect = fill.GetComponent<RectTransform>();
            slider.handleRect = handleRect;
            slider.targetGraphic = handle.GetComponent<Image>();
            return slider;
        }

        private static GameObject CreateSliderChild(string name, Transform parent, Vector2 sizeDelta)
        {
            var child = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            child.transform.SetParent(parent, false);
            var rect = child.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.sizeDelta = sizeDelta;
            return child;
        }

        private GameObject CreateUiTarget(string name, Vector2 normalizedPosition, Vector2 size)
        {
            var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            gameObject.transform.SetParent(_canvasObject.transform, false);
            var rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = normalizedPosition;
            rect.anchorMax = normalizedPosition;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            return gameObject;
        }

        private void CreatePhysics3DTarget(Vector2 normalizedPosition, List<string> events)
        {
            var camera = CreateEventCamera(typeof(PhysicsRaycaster));
            var target = new GameObject("Physics 3D Target", typeof(BoxCollider), typeof(PointerClickRecorder));
            target.transform.position = ScreenPointAtWorldDepth(camera, normalizedPosition, 10f);
            target.GetComponent<BoxCollider>().size = new Vector3(2f, 2f, 1f);
            target.GetComponent<PointerClickRecorder>().Configure(events, "physics3d.click");
            _extraObjects.Add(target);
        }

        private void CreatePhysics2DTarget(Vector2 normalizedPosition, List<string> events)
        {
            var camera = CreateEventCamera(typeof(Physics2DRaycaster));
            var target = new GameObject("Physics 2D Target", typeof(BoxCollider2D), typeof(PointerClickRecorder));
            target.transform.position = ScreenPointAtWorldDepth(camera, normalizedPosition, 10f);
            target.GetComponent<BoxCollider2D>().size = new Vector2(2f, 2f);
            target.GetComponent<PointerClickRecorder>().Configure(events, "physics2d.click");
            _extraObjects.Add(target);
        }

        private Camera CreateEventCamera(Type raycasterType)
        {
            var cameraObject = new GameObject("Event Camera", typeof(Camera), raycasterType);
            var camera = cameraObject.GetComponent<Camera>();
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.orthographic = true;
            camera.orthographicSize = 5f;
            camera.clearFlags = CameraClearFlags.Nothing;
            _extraObjects.Add(cameraObject);
            return camera;
        }

        private void DisableSceneRaycasters()
        {
            var raycasters = Object.FindObjectsOfType<BaseRaycaster>();
            for (var index = 0; index < raycasters.Length; index++)
            {
                if (!raycasters[index].enabled)
                {
                    continue;
                }

                raycasters[index].enabled = false;
                _disabledSceneRaycasters.Add(raycasters[index]);
            }
        }

        private static Vector3 ScreenPointAtWorldDepth(Camera camera, Vector2 normalizedPosition, float depth)
        {
            return camera.ScreenToWorldPoint(new Vector3(
                normalizedPosition.x * Screen.width,
                normalizedPosition.y * Screen.height,
                depth));
        }

        private static void AssertEventsInOrder(IReadOnlyList<string> actual, params string[] expected)
        {
            var nextIndex = 0;
            for (var index = 0; index < actual.Count && nextIndex < expected.Length; index++)
            {
                if (actual[index] == expected[nextIndex])
                {
                    nextIndex++;
                }
            }

            Assert.That(
                nextIndex,
                Is.EqualTo(expected.Length),
                "Expected ordered events: " + string.Join(", ", expected) + "; actual: " + string.Join(", ", actual));
        }

        private void ProcessFrame(params RadarPointerMessage[] pointers)
        {
            var frame = new RadarPointerFrameMessage();
            frame.pointers.AddRange(pointers);
            _module.InjectFrame(frame);
            _module.Process();
        }

        private void ProcessScreenFrame(RadarScreenPointerFrame frame)
        {
            _module.InjectScreenFrame(frame);
            _module.Process();
        }

        private DispatcherFixture ConfigureDispatcher()
        {
            _runtimeSettings = ScriptableObject.CreateInstance<RadarRuntimeSettings>();
            SetField(
                _runtimeSettings,
                "screens",
                new List<RadarScreenDefinition>
                {
                    new RadarScreenDefinition("front", "Front", 1920, 1080, true, true, 0)
                });
            SetField(_runtimeSettings, "screenTopologySchemaVersion", 1);

            _dispatcherObject = new GameObject("RadarFrameDispatcher", typeof(RadarFrameDispatcher));
            var dispatcher = _dispatcherObject.GetComponent<RadarFrameDispatcher>();
            var client = new FakeRadarPipeClient();
            dispatcher.ConfigureForTests(_runtimeSettings, client, false);
            _module.Dispatcher = dispatcher;
            _module.ScreenId = "front";
            return new DispatcherFixture(dispatcher, client);
        }

        private static RadarPointerBatchPayload Batch(params RadarScreenPointerFrame[] frames)
        {
            return new RadarPointerBatchPayload
            {
                screens = new List<RadarScreenPointerFrame>(frames)
            };
        }

        private static RadarScreenPointerFrame ScreenFrame(
            string id,
            bool isPrimary,
            params RadarScreenPointer[] pointers)
        {
            return new RadarScreenPointerFrame
            {
                screen = new RadarScreenInfo
                {
                    screenId = id,
                    name = id,
                    widthPixels = 1920,
                    heightPixels = 1080,
                    isPrimary = isPrimary
                },
                pointers = new System.Collections.Generic.List<RadarScreenPointer>(pointers)
            };
        }

        private static RadarScreenPointer ScreenPointer(
            int id,
            float x,
            float y,
            RadarPointerPhase phase)
        {
            return new RadarScreenPointer
            {
                pointerId = id,
                normalizedX = x,
                normalizedY = y,
                pixelX = x * 1920f,
                pixelY = y * 1080f,
                phase = phase,
                confidence = 1f
            };
        }

        private static RadarPointerMessage Pointer(int id, float x, float y, RadarPointerPhase phase)
        {
            return new RadarPointerMessage
            {
                pointerId = id,
                normalizedX = x,
                normalizedY = y,
                phase = phase,
                confidence = 1f
            };
        }

        private static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            field.SetValue(target, value);
        }

        private Component CreateBasicInteractionLogger(out Type loggerType)
        {
            loggerType = Type.GetType(
                "Blaze.Radar.Samples.RadarDemoLogger, Blaze.Radar.Sample.BasicInteraction");
            if (loggerType == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    loggerType = assembly.GetType("Blaze.Radar.Samples.RadarDemoLogger");
                    if (loggerType != null)
                    {
                        break;
                    }
                }
            }

            if (loggerType == null)
            {
                Assert.Ignore("Import the Basic Interaction sample to run its logger regression tests.");
            }

            Assert.That(loggerType.IsSubclassOf(typeof(MonoBehaviour)), Is.True);

            var loggerObject = new GameObject("RadarDemoLogger Regression Fixture");
            loggerObject.SetActive(false);
            _extraObjects.Add(loggerObject);
            return loggerObject.AddComponent(loggerType);
        }

        private static MethodInfo RequirePrivateMethod(Type type, string methodName)
        {
            var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, methodName);
            return method;
        }

        private static void SetPrivateField(object target, Type type, string fieldName, object value)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            field.SetValue(target, value);
        }

        private static int GetPrivateCollectionCount(object target, Type type, string fieldName)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            var collection = field.GetValue(target);
            Assert.That(collection, Is.Not.Null, fieldName);
            var countProperty = collection.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(countProperty, Is.Not.Null, fieldName + ".Count");
            return (int)countProperty.GetValue(collection);
        }

        private static void InvokeLoggerFrame(
            MethodInfo receiveFrame,
            object logger,
            long sequence,
            RadarScreenPointer pointer)
        {
            var frame = ScreenFrame(
                "main",
                true,
                pointer == null ? new RadarScreenPointer[] { null } : new[] { pointer });
            frame.sequence = sequence;
            frame.timestampUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            receiveFrame.Invoke(logger, new object[] { frame });
        }

        private static void InvokeLoggerEmptyFrame(MethodInfo receiveFrame, object logger, long sequence)
        {
            var frame = ScreenFrame("main", true);
            frame.sequence = sequence;
            frame.timestampUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            receiveFrame.Invoke(logger, new object[] { frame });
        }

        private readonly struct DispatcherFixture
        {
            public DispatcherFixture(RadarFrameDispatcher dispatcher, FakeRadarPipeClient client)
            {
                Dispatcher = dispatcher;
                Client = client;
            }

            public RadarFrameDispatcher Dispatcher { get; }
            public FakeRadarPipeClient Client { get; }
        }

        private sealed class FakeRadarPipeClient : IRadarPipeClient
        {
            private readonly Queue<Action> _mainThreadActions = new Queue<Action>();
            private RadarPointerBatchPayload _latestBatch;

            public bool IsConnected { get; private set; }
            public long DroppedBatchCount { get; private set; }

            public event Action<bool> ConnectionChanged;
            public event Action<string> ErrorReceived
            {
                add { }
                remove { }
            }

            public void Start(RadarHelloPayload hello)
            {
                SetConnected(true);
            }

            public bool TryConsumeLatestBatch(out RadarPointerBatchPayload batch)
            {
                batch = _latestBatch;
                _latestBatch = null;
                return batch != null;
            }

            public void DrainMainThreadEvents()
            {
                while (_mainThreadActions.Count > 0)
                {
                    _mainThreadActions.Dequeue().Invoke();
                }
            }

            public Task StopAsync()
            {
                SetConnected(false);
                _latestBatch = null;
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                _latestBatch = null;
                _mainThreadActions.Clear();
                IsConnected = false;
            }

            public void Publish(RadarPointerBatchPayload batch)
            {
                if (_latestBatch != null)
                {
                    DroppedBatchCount++;
                }

                _latestBatch = batch;
            }

            public void SetConnected(bool connected)
            {
                if (IsConnected == connected)
                {
                    return;
                }

                IsConnected = connected;
                _mainThreadActions.Enqueue(() => ConnectionChanged?.Invoke(connected));
            }
        }

        private sealed class CancelOnPointerUp : MonoBehaviour, IPointerUpHandler
        {
            public RadarInputModule Module { get; set; }

            public void OnPointerUp(PointerEventData eventData)
            {
                Module.CancelAllPointers();
            }
        }

        private sealed class DragPathRecorder : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            public List<string> Events { get; set; }

            public void OnBeginDrag(PointerEventData eventData)
            {
                Events?.Add("drag.begin");
            }

            public void OnDrag(PointerEventData eventData)
            {
                Events?.Add("drag.move");
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                Events?.Add("drag.end");
            }
        }

        private sealed class PointerClickRecorder : MonoBehaviour, IPointerClickHandler
        {
            private List<string> _events;
            private string _eventName;

            public void Configure(List<string> events, string eventName)
            {
                _events = events;
                _eventName = eventName;
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                _events?.Add(_eventName);
            }
        }
    }
}
