using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Blaze.Interaction
{
    [AddComponentMenu("Event/Interaction Input Module")]
    public class InteractionInputModule : BaseInputModule
    {
        private const int MaximumFramesPerProcess = 8;
        private const int PendingFrameCapacity = 64;

        [SerializeField] private InteractionInputMode inputMode = InteractionInputMode.InteractionOnly;
        [SerializeField, Tooltip("Surface ID consumed by this EventSystem.")]
        private string surfaceId = "main";

        private readonly Dictionary<int, PointerEventData> pointerData =
            new Dictionary<int, PointerEventData>();
        private readonly List<RaycastResult> raycastResults = new List<RaycastResult>();
        private readonly LinkedList<InteractionFrame> pendingFrames = new LinkedList<InteractionFrame>();
        private InteractionManager manager;

        public InteractionInputMode InputMode
        {
            get { return inputMode; }
            set { inputMode = value; }
        }

        public string SurfaceId
        {
            get { return surfaceId; }
            set
            {
                var next = value == null ? string.Empty : value.Trim();
                if (string.Equals(surfaceId, next, StringComparison.OrdinalIgnoreCase))
                {
                    surfaceId = next;
                    return;
                }

                CancelAllPointers();
                surfaceId = next;
            }
        }

        public InteractionManager Manager
        {
            get { return manager; }
            set
            {
                if (ReferenceEquals(manager, value))
                {
                    return;
                }

                UnsubscribeManager();
                manager = value;
                SubscribeManager();
            }
        }

        public IReadOnlyDictionary<int, PointerEventData> ActivePointers { get { return pointerData; } }
        internal int PendingFrameCount { get { return pendingFrames.Count; } }

        public override bool IsPointerOverGameObject(int pointerId)
        {
            PointerEventData data;
            return pointerData.TryGetValue(pointerId, out data) && data.pointerEnter != null;
        }

        public override void DeactivateModule()
        {
            CancelAllPointers();
            base.DeactivateModule();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (manager == null)
            {
                manager = InteractionManager.Instance;
            }

            SubscribeManager();
        }

        protected override void OnDisable()
        {
            UnsubscribeManager();
            CancelAllPointers();
            base.OnDisable();
        }

        public override void Process()
        {
            var processed = 0;
            while (processed < MaximumFramesPerProcess && pendingFrames.First != null)
            {
                var frame = pendingFrames.First.Value;
                pendingFrames.RemoveFirst();
                ProcessFrame(frame);
                processed++;
            }

            if (inputMode == InteractionInputMode.InteractionAndMouseDebug)
            {
                ProcessMouseDebug();
            }
        }

        public void InjectFrame(InteractionFrame frame)
        {
            if (frame == null || !IsSelectedSurface(frame.SurfaceId))
            {
                return;
            }

            EnqueueFrame(frame);
        }

        public void CancelAllPointers()
        {
            pendingFrames.Clear();
            var activePointers = new List<PointerEventData>(pointerData.Values);
            pointerData.Clear();
            for (var index = 0; index < activePointers.Count; index++)
            {
                CancelPointer(activePointers[index]);
            }
        }

        private void EnqueueFrame(InteractionFrame frame)
        {
            var lifecycle = ContainsLifecycleEdge(frame);
            if (!lifecycle && pendingFrames.Last != null && !ContainsLifecycleEdge(pendingFrames.Last.Value))
            {
                pendingFrames.Last.Value = frame;
                return;
            }

            if (pendingFrames.Count >= PendingFrameCapacity)
            {
                var visual = pendingFrames.First;
                while (visual != null && ContainsLifecycleEdge(visual.Value))
                {
                    visual = visual.Next;
                }

                if (visual != null)
                {
                    pendingFrames.Remove(visual);
                }
                else if (!lifecycle)
                {
                    return;
                }
                else
                {
                    var oldest = pendingFrames.First.Value;
                    pendingFrames.RemoveFirst();
                    ProcessFrame(oldest);
                }
            }

            pendingFrames.AddLast(frame);
        }

        private static bool ContainsLifecycleEdge(InteractionFrame frame)
        {
            if (frame == null || frame.Points == null)
            {
                return false;
            }

            for (var index = 0; index < frame.Points.Count; index++)
            {
                var point = frame.Points[index];
                if (point != null &&
                    (point.Phase == InteractionPhase.Down ||
                     point.Phase == InteractionPhase.Up ||
                     point.Phase == InteractionPhase.Cancel))
                {
                    return true;
                }
            }

            return false;
        }

        private void ProcessFrame(InteractionFrame frame)
        {
            if (frame == null || frame.Points == null || !IsSelectedSurface(frame.SurfaceId))
            {
                return;
            }

            for (var index = 0; index < frame.Points.Count; index++)
            {
                var point = frame.Points[index];
                if (point == null || !IsSelectedSurface(point.SurfaceId) || point.NormalizedPosition == null)
                {
                    continue;
                }

                ProcessPointer(
                    ToPointerId(point.Id),
                    new Vector2(
                        Mathf.Clamp01(point.NormalizedPosition.X) * Screen.width,
                        Mathf.Clamp01(point.NormalizedPosition.Y) * Screen.height),
                    point.Phase,
                    Vector2.zero);
            }
        }

        private bool IsSelectedSurface(string candidate)
        {
            return !string.IsNullOrWhiteSpace(candidate) &&
                   (string.IsNullOrWhiteSpace(surfaceId) ||
                    string.Equals(surfaceId, candidate, StringComparison.OrdinalIgnoreCase));
        }

        private void ProcessMouseDebug()
        {
            var phase = InteractionPhase.Hover;
            if (Input.GetMouseButtonDown(0))
            {
                phase = InteractionPhase.Down;
            }
            else if (Input.GetMouseButtonUp(0))
            {
                phase = InteractionPhase.Up;
            }
            else if (Input.GetMouseButton(0))
            {
                phase = InteractionPhase.Move;
            }

            ProcessPointer(-1, Input.mousePosition, phase, Input.mouseScrollDelta);
        }

        private void ProcessPointer(
            int pointerId,
            Vector2 screenPosition,
            InteractionPhase phase,
            Vector2 scrollDelta)
        {
            PointerEventData data;
            if (!pointerData.TryGetValue(pointerId, out data))
            {
                if (phase == InteractionPhase.Up || phase == InteractionPhase.Cancel)
                {
                    return;
                }

                data = new PointerEventData(eventSystem)
                {
                    pointerId = pointerId,
                    position = screenPosition,
                    pressPosition = screenPosition,
                    button = PointerEventData.InputButton.Left,
                    useDragThreshold = true
                };
                pointerData.Add(pointerId, data);
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
                case InteractionPhase.Down:
                    ProcessPress(data, currentOverGo);
                    break;
                case InteractionPhase.Move:
                    ProcessMoveAndDrag(data);
                    break;
                case InteractionPhase.Up:
                    ProcessRelease(data, currentOverGo);
                    pointerData.Remove(pointerId);
                    break;
                case InteractionPhase.Cancel:
                    pointerData.Remove(pointerId);
                    CancelPointer(data);
                    break;
                case InteractionPhase.Hover:
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
            raycastResults.Clear();
            eventSystem.RaycastAll(data, raycastResults);
            var result = FindFirstRaycast(raycastResults);
            raycastResults.Clear();
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
            data.clickCount = pressed == data.lastPress && now - data.clickTime < .3f
                ? data.clickCount + 1
                : 1;
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

            ResetPointer(data);
            HandlePointerExitAndEnter(data, null);
        }

        private void CancelPointer(PointerEventData data)
        {
            if (data.pointerPress != null)
            {
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
            }

            if (data.pointerDrag != null && data.dragging)
            {
                ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.endDragHandler);
            }

            ResetPointer(data);
            HandlePointerExitAndEnter(data, null);
        }

        private static void ResetPointer(PointerEventData data)
        {
            data.eligibleForClick = false;
            data.pointerPress = null;
            data.rawPointerPress = null;
            data.pointerDrag = null;
            data.dragging = false;
        }

        private void SubscribeManager()
        {
            if (manager == null)
            {
                return;
            }

            manager.PointAdded -= OnPoint;
            manager.PointUpdated -= OnPoint;
            manager.PointRemoved -= OnPoint;
            manager.ConnectionChanged -= OnConnectionChanged;
            manager.PointAdded += OnPoint;
            manager.PointUpdated += OnPoint;
            manager.PointRemoved += OnPoint;
            manager.ConnectionChanged += OnConnectionChanged;
        }

        private void UnsubscribeManager()
        {
            if (manager == null)
            {
                return;
            }

            manager.PointAdded -= OnPoint;
            manager.PointUpdated -= OnPoint;
            manager.PointRemoved -= OnPoint;
            manager.ConnectionChanged -= OnConnectionChanged;
        }

        private void OnPoint(InteractionPoint point)
        {
            if (point == null || !IsSelectedSurface(point.SurfaceId))
            {
                return;
            }

            InjectFrame(new InteractionFrame
            {
                ProviderId = point.ProviderId,
                ProviderInstanceId = point.ProviderInstanceId,
                SurfaceId = point.SurfaceId,
                Sequence = point.TimestampUnixMs,
                TimestampUnixMs = point.TimestampUnixMs,
                Points = new List<InteractionPoint> { point }
            });
        }

        private void OnConnectionChanged(bool connected)
        {
            if (!connected)
            {
                CancelAllPointers();
            }
        }

        private static int ToPointerId(long pointId)
        {
            return unchecked((int)pointId);
        }

        private static bool ShouldStartDrag(
            Vector2 pressPosition,
            Vector2 currentPosition,
            float threshold,
            bool useDragThreshold)
        {
            return !useDragThreshold ||
                   (pressPosition - currentPosition).sqrMagnitude >= threshold * threshold;
        }
    }
}
