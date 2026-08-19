using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Blaze.Radar
{
    [AddComponentMenu("Event/Radar Input Module")]
    public sealed class RadarInputModule : BaseInputModule
    {
        private const int MaximumFramesPerProcess = 8;
        private const int PendingScreenFrameCapacity = 64;

        [Header("Radar Source")]
        [SerializeField, Tooltip("Dispatcher that supplies normalized radar pointer frames.")]
        private RadarFrameDispatcher dispatcher;

        [SerializeField, Tooltip("RadarOnly for production; RadarAndMouseDebug keeps the standard mouse available for scene testing.")]
        private RadarInputMode inputMode = RadarInputMode.RadarOnly;

        [SerializeField, Tooltip("Logical screen ID consumed by this EventSystem. Leave empty to consume the primary screen.")]
        private string screenId = string.Empty;

        private readonly Dictionary<int, PointerEventData> _pointerData =
            new Dictionary<int, PointerEventData>();
        private readonly List<RaycastResult> _raycastResults = new List<RaycastResult>();
        private readonly LinkedList<RadarScreenPointerFrame> _pendingScreenFrames =
            new LinkedList<RadarScreenPointerFrame>();
        private RadarPointerFrameMessage _pendingFrame;
        private bool _dispatcherConnected = true;

        public RadarFrameDispatcher Dispatcher
        {
            get => dispatcher;
            set
            {
                if (dispatcher == value)
                {
                    return;
                }

                UnsubscribeDispatcher();
                dispatcher = value;
                SubscribeDispatcher();
            }
        }

        public RadarInputMode InputMode
        {
            get => inputMode;
            set => inputMode = value;
        }

        public string ScreenId
        {
            get => screenId;
            set
            {
                var next = value == null ? string.Empty : value.Trim();
                if (string.Equals(screenId, next, StringComparison.OrdinalIgnoreCase))
                {
                    screenId = next;
                    return;
                }

                CancelAllPointers();
                screenId = next;
            }
        }

        public IReadOnlyDictionary<int, PointerEventData> ActivePointers => _pointerData;

        public override bool IsPointerOverGameObject(int pointerId)
        {
            return _pointerData.TryGetValue(pointerId, out var data) && data.pointerEnter != null;
        }

        public override void DeactivateModule()
        {
            CancelAllPointers();
            base.DeactivateModule();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (dispatcher == null)
            {
                dispatcher = FindObjectOfType<RadarFrameDispatcher>();
            }

            SubscribeDispatcher();
        }

        protected override void OnDisable()
        {
            UnsubscribeDispatcher();
            CancelAllPointers();
            base.OnDisable();
        }

        public void CancelAllPointers()
        {
            _pendingFrame = null;
            _pendingScreenFrames.Clear();
            var activePointers = new List<PointerEventData>(_pointerData.Values);
            _pointerData.Clear();

            foreach (var data in activePointers)
            {
                if (data.pointerPress != null)
                {
                    ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                }

                if (data.pointerDrag != null && data.dragging)
                {
                    ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.endDragHandler);
                }

                data.eligibleForClick = false;
                data.pointerPress = null;
                data.rawPointerPress = null;
                data.pointerDrag = null;
                data.dragging = false;
                HandlePointerExitAndEnter(data, null);
            }
        }

        public override void Process()
        {
            var processedFrames = 0;
            while (processedFrames < MaximumFramesPerProcess && _pendingScreenFrames.First != null)
            {
                var frame = _pendingScreenFrames.First.Value;
                _pendingScreenFrames.RemoveFirst();
                ProcessScreenFrame(frame);
                processedFrames++;
            }

            if (processedFrames == 0 && _pendingFrame != null)
            {
                var frame = _pendingFrame;
                _pendingFrame = null;
                ProcessRadarFrame(frame);
            }

            if (inputMode == RadarInputMode.RadarAndMouseDebug)
            {
                ProcessMouseDebug();
            }
        }

        public void InjectFrame(RadarPointerFrameMessage frame)
        {
            _pendingScreenFrames.Clear();
            _pendingFrame = frame;
        }

        internal void InjectScreenFrame(RadarScreenPointerFrame frame)
        {
            OnScreenFrameReceived(frame);
        }

        private void OnScreenFrameReceived(RadarScreenPointerFrame frame)
        {
            if (dispatcher != null && !_dispatcherConnected)
            {
                return;
            }

            if (!IsSelectedScreen(frame))
            {
                return;
            }

            _pendingFrame = null;
            EnqueueScreenFrame(frame);
        }

        private void EnqueueScreenFrame(RadarScreenPointerFrame frame)
        {
            var lifecycle = ContainsLifecycleEdge(frame);
            if (!lifecycle && _pendingScreenFrames.Last != null &&
                !ContainsLifecycleEdge(_pendingScreenFrames.Last.Value))
            {
                _pendingScreenFrames.Last.Value = frame;
                return;
            }

            if (_pendingScreenFrames.Count >= PendingScreenFrameCapacity)
            {
                var visual = _pendingScreenFrames.First;
                while (visual != null && ContainsLifecycleEdge(visual.Value))
                {
                    visual = visual.Next;
                }

                if (visual != null)
                {
                    _pendingScreenFrames.Remove(visual);
                }
                else if (!lifecycle)
                {
                    return;
                }
                else
                {
                    // Dispatcher callbacks run on Unity's main thread. Processing the oldest edge here
                    // is the bounded fallback when EventSystem.Process has been stalled for many frames.
                    var oldest = _pendingScreenFrames.First.Value;
                    _pendingScreenFrames.RemoveFirst();
                    ProcessScreenFrame(oldest);
                }
            }

            _pendingScreenFrames.AddLast(frame);
        }

        private static bool ContainsLifecycleEdge(RadarScreenPointerFrame frame)
        {
            if (frame == null || frame.pointers == null)
            {
                return false;
            }

            for (var index = 0; index < frame.pointers.Count; index++)
            {
                var pointer = frame.pointers[index];
                if (pointer != null &&
                    (pointer.phase == RadarPointerPhase.Down || pointer.phase == RadarPointerPhase.Up))
                {
                    return true;
                }
            }

            return false;
        }

        internal int PendingScreenFrameCount => _pendingScreenFrames.Count;

        private void ProcessRadarFrame(RadarPointerFrameMessage frame)
        {
            if (frame?.pointers == null)
            {
                return;
            }

            for (var index = 0; index < frame.pointers.Count; index++)
            {
                var message = frame.pointers[index];
                if (message == null)
                {
                    continue;
                }

                var screenPosition = new Vector2(
                    Mathf.Clamp01(message.normalizedX) * Screen.width,
                    Mathf.Clamp01(message.normalizedY) * Screen.height);
                ProcessPointer(message.pointerId, screenPosition, message.phase, Vector2.zero);
            }
        }

        private void ProcessScreenFrame(RadarScreenPointerFrame frame)
        {
            if (frame == null || frame.pointers == null)
            {
                return;
            }

            for (var index = 0; index < frame.pointers.Count; index++)
            {
                var pointer = frame.pointers[index];
                if (pointer == null)
                {
                    continue;
                }

                var eventPosition = new Vector2(
                    Mathf.Clamp01(pointer.normalizedX) * Screen.width,
                    Mathf.Clamp01(pointer.normalizedY) * Screen.height);
                ProcessPointer(pointer.pointerId, eventPosition, pointer.phase, Vector2.zero);
            }
        }

        private bool IsSelectedScreen(RadarScreenPointerFrame frame)
        {
            if (frame == null || frame.screen == null || string.IsNullOrWhiteSpace(frame.screen.screenId))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(screenId)
                ? frame.screen.isPrimary
                : string.Equals(frame.screen.screenId, screenId, StringComparison.OrdinalIgnoreCase);
        }

        private void ProcessMouseDebug()
        {
            var phase = RadarPointerPhase.Hover;
            if (Input.GetMouseButtonDown(0))
            {
                phase = RadarPointerPhase.Down;
            }
            else if (Input.GetMouseButtonUp(0))
            {
                phase = RadarPointerPhase.Up;
            }
            else if (Input.GetMouseButton(0))
            {
                phase = RadarPointerPhase.Move;
            }

            ProcessPointer(-1, Input.mousePosition, phase, Input.mouseScrollDelta);
        }

        private void ProcessPointer(
            int pointerId,
            Vector2 screenPosition,
            RadarPointerPhase phase,
            Vector2 scrollDelta)
        {
            if (!_pointerData.TryGetValue(pointerId, out var data))
            {
                data = new PointerEventData(eventSystem)
                {
                    pointerId = pointerId,
                    position = screenPosition,
                    pressPosition = screenPosition,
                    button = PointerEventData.InputButton.Left,
                    useDragThreshold = true
                };
                _pointerData.Add(pointerId, data);
            }

            data.delta = screenPosition - data.position;
            data.position = screenPosition;
            data.scrollDelta = scrollDelta;
            data.pointerCurrentRaycast = Raycast(data);
            var currentOverGo = data.pointerCurrentRaycast.gameObject;
            HandlePointerExitAndEnter(data, currentOverGo);

            if (scrollDelta.sqrMagnitude > 0f && currentOverGo != null)
            {
                var scrollHandler = ExecuteEvents.GetEventHandler<IScrollHandler>(currentOverGo);
                ExecuteEvents.ExecuteHierarchy(scrollHandler, data, ExecuteEvents.scrollHandler);
            }

            switch (phase)
            {
                case RadarPointerPhase.Down:
                    ProcessPress(data, currentOverGo);
                    break;
                case RadarPointerPhase.Move:
                    ProcessMoveAndDrag(data);
                    break;
                case RadarPointerPhase.Up:
                    ProcessRelease(data, currentOverGo);
                    _pointerData.Remove(pointerId);
                    break;
                case RadarPointerPhase.Hover:
                    if (data.IsPointerMoving())
                    {
                        ProcessMoveAndDrag(data);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(phase), phase, null);
            }
        }

        private RaycastResult Raycast(PointerEventData data)
        {
            _raycastResults.Clear();
            eventSystem.RaycastAll(data, _raycastResults);
            var result = FindFirstRaycast(_raycastResults);
            _raycastResults.Clear();
            return result;
        }

        private void ProcessPress(PointerEventData data, GameObject currentOverGo)
        {
            data.eligibleForClick = true;
            data.delta = Vector2.zero;
            data.dragging = false;
            data.useDragThreshold = true;
            data.pressPosition = data.position;
            data.pointerPressRaycast = data.pointerCurrentRaycast;
            DeselectIfSelectionChanged(currentOverGo, data);

            var pressed = ExecuteEvents.ExecuteHierarchy(currentOverGo, data, ExecuteEvents.pointerDownHandler);
            if (pressed == null)
            {
                pressed = ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentOverGo);
            }

            var now = Time.unscaledTime;
            if (pressed == data.lastPress && now - data.clickTime < 0.3f)
            {
                data.clickCount++;
            }
            else
            {
                data.clickCount = 1;
            }

            data.clickTime = now;
            data.pointerPress = pressed;
            data.rawPointerPress = currentOverGo;
            data.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(currentOverGo);
            if (data.pointerDrag != null)
            {
                ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.initializePotentialDrag);
            }
        }

        private void DeselectIfSelectionChanged(GameObject currentOverGo, BaseEventData pointerEvent)
        {
            var selectHandler = ExecuteEvents.GetEventHandler<ISelectHandler>(currentOverGo);
            if (selectHandler != eventSystem.currentSelectedGameObject)
            {
                eventSystem.SetSelectedGameObject(null, pointerEvent);
            }
        }

        private void ProcessMoveAndDrag(PointerEventData data)
        {
            if (data.pointerDrag == null || !data.IsPointerMoving())
            {
                return;
            }

            if (!data.dragging && ShouldStartDrag(
                    data.pressPosition,
                    data.position,
                    eventSystem.pixelDragThreshold,
                    data.useDragThreshold))
            {
                ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.beginDragHandler);
                data.dragging = true;
            }

            if (!data.dragging)
            {
                return;
            }

            if (data.pointerPress != data.pointerDrag)
            {
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                data.eligibleForClick = false;
                data.pointerPress = null;
                data.rawPointerPress = null;
            }

            ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.dragHandler);
        }

        private void ProcessRelease(PointerEventData data, GameObject currentOverGo)
        {
            ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
            var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentOverGo);
            if (data.pointerPress == clickHandler && data.eligibleForClick)
            {
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerClickHandler);
            }
            else if (data.pointerDrag != null && data.dragging)
            {
                ExecuteEvents.ExecuteHierarchy(currentOverGo, data, ExecuteEvents.dropHandler);
            }

            if (data.pointerDrag != null && data.dragging)
            {
                ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.endDragHandler);
            }

            data.eligibleForClick = false;
            data.pointerPress = null;
            data.rawPointerPress = null;
            data.pointerDrag = null;
            data.dragging = false;
            HandlePointerExitAndEnter(data, null);
        }

        private void SubscribeDispatcher()
        {
            _dispatcherConnected = dispatcher == null || dispatcher.IsConnected;
            if (dispatcher != null)
            {
                dispatcher.ScreenFrameReceived -= OnScreenFrameReceived;
                dispatcher.ScreenFrameReceived += OnScreenFrameReceived;
                dispatcher.ConnectionChanged -= OnConnectionChanged;
                dispatcher.ConnectionChanged += OnConnectionChanged;
            }
        }

        private void UnsubscribeDispatcher()
        {
            if (dispatcher != null)
            {
                dispatcher.ScreenFrameReceived -= OnScreenFrameReceived;
                dispatcher.ConnectionChanged -= OnConnectionChanged;
            }
        }

        private void OnConnectionChanged(bool connected)
        {
            _dispatcherConnected = connected;
            if (!connected)
            {
                CancelAllPointers();
            }
        }

        private static bool ShouldStartDrag(Vector2 pressPosition, Vector2 currentPosition, float threshold, bool useDragThreshold)
        {
            return !useDragThreshold || (pressPosition - currentPosition).sqrMagnitude >= threshold * threshold;
        }
    }
}
