using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Blaze.Radar.Tests
{
    public sealed class RadarInputModulePlayModeTests
    {
        private GameObject _eventSystemObject;
        private GameObject _canvasObject;
        private RadarInputModule _module;

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
            if (Application.isPlaying)
            {
                Object.Destroy(_canvasObject);
                Object.Destroy(_eventSystemObject);
            }
            else
            {
                Object.DestroyImmediate(_canvasObject);
                Object.DestroyImmediate(_eventSystemObject);
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

        private Button CreateButton(string name, Vector2 normalizedPosition)
        {
            var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            gameObject.transform.SetParent(_canvasObject.transform, false);
            var rect = gameObject.GetComponent<RectTransform>();
            rect.anchorMin = normalizedPosition;
            rect.anchorMax = normalizedPosition;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(180f, 90f);
            return gameObject.GetComponent<Button>();
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

        private sealed class CancelOnPointerUp : MonoBehaviour, IPointerUpHandler
        {
            public RadarInputModule Module { get; set; }

            public void OnPointerUp(PointerEventData eventData)
            {
                Module.CancelAllPointers();
            }
        }
    }
}
