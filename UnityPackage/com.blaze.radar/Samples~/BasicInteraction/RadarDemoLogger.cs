using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace Blaze.Radar.Samples
{
    /// <summary>
    /// Shows primary-screen IPC frames next to the native EventSystem callbacks they produce.
    /// The live panel is lossless; bounded history keeps long field tests readable.
    /// </summary>
    public sealed class RadarDemoLogger : MonoBehaviour
    {
        private const int MaximumFrameLogLines = 200;
        private const int MaximumEventSystemLogLines = 300;
        private const float MoveLogIntervalSeconds = 0.1f;
        private static readonly Color ConnectedColor = new Color32(82, 224, 179, 255);
        private static readonly Color DisconnectedColor = new Color32(247, 184, 89, 255);

        [Header("Radar Source")]
        [SerializeField] private RadarFrameDispatcher radarDispatcher;
        [SerializeField] private RadarInputModule radarInputModule;
        [SerializeField] private string primaryScreenId = "main";

        [Header("Diagnostics UI")]
        [SerializeField] private Image connectionStatusDot;
        [SerializeField] private Text connectionStatusText;
        [SerializeField] private Text latestFrameText;
        [SerializeField] private Text eventLogText;
        [SerializeField] private Text interactionStatusText;
        [SerializeField] private ScrollRect eventLogScrollRect;

        [Header("History Limits")]
        [SerializeField, Range(1, MaximumFrameLogLines)]
        private int maxFrameLogLines = MaximumFrameLogLines;

        [FormerlySerializedAs("maxLogEntries")]
        [SerializeField, Range(1, MaximumEventSystemLogLines)]
        private int maxEventSystemLogLines = MaximumEventSystemLogLines;

        [FormerlySerializedAs("frameHistoryIntervalSeconds")]
        [SerializeField, HideInInspector] private float legacyFrameHistoryIntervalSeconds = MoveLogIntervalSeconds;

        private readonly Queue<string> _frameEntries = new Queue<string>();
        private readonly Queue<string> _eventEntries = new Queue<string>();
        private readonly Dictionary<string, float> _nextMoveLogTimes = new Dictionary<string, float>();
        private readonly Dictionary<string, RadarPointerPhase> _lastFramePhases =
            new Dictionary<string, RadarPointerPhase>();
        private readonly HashSet<string> _seenPointerKeys = new HashSet<string>();
        private readonly List<string> _stalePointerKeys = new List<string>();
        private readonly Dictionary<string, float> _nextContinuousEventTimes = new Dictionary<string, float>();
        private readonly StringBuilder _textBuilder = new StringBuilder(8192);

        private RadarScreenPointerFrame _latestPrimaryFrame;
        private long _receivedPrimaryFrameCount;
        private long _legacyCompatibilityFrameCount;
        private bool? _lastConnectedState;
        private bool _sessionStarted;
        private bool _isAutoScrolling;
        private int _ignoreScrollCallbacksThroughFrame = -1;

        public bool IsAutoScrolling =>
            _isAutoScrolling || Time.frameCount <= _ignoreScrollCallbacksThroughFrame;

        private int FrameHistoryLimit => Mathf.Clamp(maxFrameLogLines, 1, MaximumFrameLogLines);

        private int EventSystemHistoryLimit =>
            Mathf.Clamp(maxEventSystemLogLines, 1, MaximumEventSystemLogLines);

        private void OnEnable()
        {
            if (radarDispatcher == null)
            {
                SetConnectionPresentation(false);
                AppendEventLog("ERROR", "RadarFrameDispatcher reference is missing.");
                RenderLivePanel();
                return;
            }

            radarDispatcher.ConnectionChanged += OnConnectionChanged;
            radarDispatcher.ErrorReceived += OnRadarError;
            radarDispatcher.ScreenFrameReceived += OnScreenFrameReceived;
            radarDispatcher.PointerFrameReceived += OnLegacyPointerFrameReceived;

            if (!_sessionStarted)
            {
                _sessionStarted = true;
                AppendEventLog(
                    "SESSION",
                    $"SDK {UnitySdkVersion.Value} | Unity {Application.unityVersion} | "
                    + $"player {Screen.width}x{Screen.height}");
            }

            OnConnectionChanged(radarDispatcher.IsConnected);
            ShowInteractionStatus("Ready | use mouse debug or Bridge Simulation to drive native EventSystem targets");
            RenderLivePanel();
        }

        private void OnDisable()
        {
            _nextMoveLogTimes.Clear();
            _lastFramePhases.Clear();
            if (radarDispatcher == null)
            {
                return;
            }

            radarDispatcher.ConnectionChanged -= OnConnectionChanged;
            radarDispatcher.ErrorReceived -= OnRadarError;
            radarDispatcher.ScreenFrameReceived -= OnScreenFrameReceived;
            radarDispatcher.PointerFrameReceived -= OnLegacyPointerFrameReceived;
        }

        private void OnValidate()
        {
            maxFrameLogLines = Mathf.Clamp(maxFrameLogLines, 1, MaximumFrameLogLines);
            maxEventSystemLogLines = Mathf.Clamp(
                maxEventSystemLogLines,
                1,
                MaximumEventSystemLogLines);
            legacyFrameHistoryIntervalSeconds = MoveLogIntervalSeconds;
            primaryScreenId = primaryScreenId == null ? string.Empty : primaryScreenId.Trim();
        }

        public void ClearLog()
        {
            _frameEntries.Clear();
            _eventEntries.Clear();
            _nextMoveLogTimes.Clear();
            _lastFramePhases.Clear();
            _nextContinuousEventTimes.Clear();
            AppendEventLog("SESSION", "History cleared. Live primary-screen diagnostics remain active.");
        }

        public void ShowInteractionStatus(string message)
        {
            if (interactionStatusText != null)
            {
                interactionStatusText.text = string.IsNullOrWhiteSpace(message) ? "Ready" : message;
            }
        }

        public void LogUiEvent(string eventName, string details)
        {
            LogUiEvent(eventName, details, false);
        }

        public void LogContinuousUiEvent(string eventName, string details)
        {
            LogUiEvent(eventName, details, true);
        }

        public void LogPointerEvent(
            string source,
            string eventName,
            PointerEventData eventData,
            bool continuous = false)
        {
            if (eventData == null)
            {
                return;
            }

            string target = eventData.pointerCurrentRaycast.gameObject != null
                ? eventData.pointerCurrentRaycast.gameObject.name
                : eventData.pointerEnter != null ? eventData.pointerEnter.name : "<none>";
            string summary = $"{source} | {eventName} | pointer {eventData.pointerId} | hit {target}";
            ShowInteractionStatus(summary);

            string throttleKey = $"POINTER:{source}:{eventName}:{eventData.pointerId}";
            if (continuous && !ShouldRecordContinuousEvent(throttleKey))
            {
                return;
            }

            AppendEventLog(
                "EVENT",
                $"{source}.{eventName} | id {eventData.pointerId} | "
                + $"pos ({eventData.position.x:0.0}, {eventData.position.y:0.0}) | "
                + $"delta ({eventData.delta.x:0.0}, {eventData.delta.y:0.0}) | "
                + $"press ({eventData.pressPosition.x:0.0}, {eventData.pressPosition.y:0.0}) | "
                + $"hit {target} | clicks {eventData.clickCount} | dragging {eventData.dragging}");
        }

        private void LogUiEvent(string eventName, string details, bool continuous)
        {
            string message = $"{eventName} | {details}";
            ShowInteractionStatus(message);

            if (continuous && !ShouldRecordContinuousEvent("UGUI:" + eventName))
            {
                return;
            }

            AppendEventLog("UGUI", message);
        }

        private bool ShouldRecordContinuousEvent(string key)
        {
            float now = Time.unscaledTime;
            if (_nextContinuousEventTimes.TryGetValue(key, out float nextTime) && now < nextTime)
            {
                return false;
            }

            _nextContinuousEventTimes[key] = now + MoveLogIntervalSeconds;
            return true;
        }

        private void OnConnectionChanged(bool connected)
        {
            if (!connected)
            {
                _nextMoveLogTimes.Clear();
                _lastFramePhases.Clear();
            }

            SetConnectionPresentation(connected);
            if (!_lastConnectedState.HasValue || _lastConnectedState.Value != connected)
            {
                _lastConnectedState = connected;
                RadarPipeClient client = radarDispatcher != null ? radarDispatcher.Client : null;
                string bridgeVersion = ValueOrUnknown(client != null ? client.BridgeVersion : null);
                string deviceModel = ValueOrUnknown(client != null ? client.DeviceModel : null);
                AppendEventLog(
                    "IPC",
                    connected
                        ? $"CONNECTED | bridge {bridgeVersion} | device {deviceModel}"
                        : "DISCONNECTED | reconnect loop remains active");
            }

            RenderLivePanel();
        }

        private void SetConnectionPresentation(bool connected)
        {
            Color color = connected ? ConnectedColor : DisconnectedColor;
            if (connectionStatusDot != null)
            {
                connectionStatusDot.color = color;
            }

            if (connectionStatusText != null)
            {
                connectionStatusText.text = connected ? "IPC  CONNECTED" : "IPC  DISCONNECTED";
                connectionStatusText.color = color;
            }
        }

        private void OnRadarError(string message)
        {
            string safeMessage = string.IsNullOrWhiteSpace(message) ? "Unknown RadarBridge error." : message;
            AppendEventLog("ERROR", safeMessage);
            ShowInteractionStatus("Radar error | " + safeMessage);
        }

        private void OnScreenFrameReceived(RadarScreenPointerFrame frame)
        {
            if (!IsConfiguredPrimaryFrame(frame))
            {
                return;
            }

            _latestPrimaryFrame = frame;
            _receivedPrimaryFrameCount++;
            RenderLivePanel();
            AppendFrameHistory(frame);
        }

        private void OnLegacyPointerFrameReceived(RadarPointerFrameMessage frame)
        {
            if (frame == null)
            {
                return;
            }

            _legacyCompatibilityFrameCount++;
            RenderLivePanel();
        }

        private bool IsConfiguredPrimaryFrame(RadarScreenPointerFrame frame)
        {
            if (frame == null || frame.screen == null || string.IsNullOrWhiteSpace(frame.screen.screenId))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(primaryScreenId)
                ? frame.screen.isPrimary
                : string.Equals(frame.screen.screenId, primaryScreenId, StringComparison.OrdinalIgnoreCase);
        }

        private void RenderLivePanel()
        {
            if (latestFrameText == null)
            {
                return;
            }

            RadarPipeClient client = radarDispatcher != null ? radarDispatcher.Client : null;
            bool connected = radarDispatcher != null && radarDispatcher.IsConnected;
            long droppedBatches = client != null ? client.DroppedBatchCount : 0L;
            RadarScreenPointerFrame frame = _latestPrimaryFrame;

            _textBuilder.Clear();
            _textBuilder.Append("IPC: ").Append(connected ? "CONNECTED" : "DISCONNECTED").AppendLine();

            if (frame != null && frame.screen != null)
            {
                int pointerCount = frame.pointers != null ? frame.pointers.Count : 0;
                _textBuilder.Append("PRIMARY: ").Append(frame.screen.screenId)
                    .Append(" · ").Append(ValueOrUnknown(frame.screen.name))
                    .Append(" · ").Append(frame.screen.widthPixels).Append('×').Append(frame.screen.heightPixels)
                    .AppendLine();
                _textBuilder.Append("FRAME: ").Append(frame.sequence)
                    .Append(" · age ").Append(FormatAge(frame.timestampUnixMilliseconds))
                    .Append(" · pointers ").Append(pointerCount)
                    .Append(" · dropped batches ").Append(droppedBatches)
                    .AppendLine();
            }
            else
            {
                _textBuilder.Append("PRIMARY: ").Append(ValueOrUnknown(primaryScreenId))
                    .Append(" · waiting for screen metadata").AppendLine();
                _textBuilder.Append("FRAME: waiting for the first primary-screen batch").AppendLine();
            }

            _textBuilder.Append("INPUT: ")
                .Append(radarInputModule != null ? radarInputModule.InputMode.ToString() : "unknown")
                .Append(" · received ").Append(_receivedPrimaryFrameCount)
                .Append(" · compatibility primary frames ").Append(_legacyCompatibilityFrameCount);

            if (frame != null && frame.pointers != null)
            {
                for (int index = 0; index < frame.pointers.Count; index++)
                {
                    RadarScreenPointer pointer = frame.pointers[index];
                    if (pointer == null)
                    {
                        continue;
                    }

                    _textBuilder.AppendLine();
                    AppendPointerDetails(_textBuilder, frame, pointer);
                }
            }

            latestFrameText.text = _textBuilder.ToString();
        }

        private void AppendFrameHistory(RadarScreenPointerFrame frame)
        {
            int pointerCount = frame.pointers != null ? frame.pointers.Count : 0;
            float now = Time.unscaledTime;
            _seenPointerKeys.Clear();
            if (pointerCount == 0)
            {
                RemoveMissingPointerStates(frame.screen.screenId);
                AppendFrameLog(
                    $"[{frame.screen.screenId}] seq {frame.sequence} | no active pointers | age {FormatAge(frame.timestampUnixMilliseconds)}");
                return;
            }

            for (int index = 0; index < frame.pointers.Count; index++)
            {
                RadarScreenPointer pointer = frame.pointers[index];
                if (pointer == null)
                {
                    AppendEventLog("ERROR", $"Primary frame {frame.sequence} contains a null pointer at index {index}.");
                    continue;
                }

                string moveKey = frame.screen.screenId + ":" + pointer.pointerId;
                _seenPointerKeys.Add(moveKey);
                bool isMove = pointer.phase == RadarPointerPhase.Move;
                bool isConsecutiveMove = isMove
                    && _lastFramePhases.TryGetValue(moveKey, out RadarPointerPhase previousPhase)
                    && previousPhase == RadarPointerPhase.Move;
                if (isConsecutiveMove
                    && _nextMoveLogTimes.TryGetValue(moveKey, out float nextTime)
                    && now < nextTime)
                {
                    continue;
                }

                if (isMove)
                {
                    _nextMoveLogTimes[moveKey] = now + MoveLogIntervalSeconds;
                    _lastFramePhases[moveKey] = RadarPointerPhase.Move;
                }
                else
                {
                    _nextMoveLogTimes.Remove(moveKey);
                    if (pointer.phase == RadarPointerPhase.Up)
                    {
                        _lastFramePhases.Remove(moveKey);
                    }
                    else
                    {
                        _lastFramePhases[moveKey] = pointer.phase;
                    }
                }

                _textBuilder.Clear();
                _textBuilder.Append("seq ").Append(frame.sequence)
                    .Append(" | age ").Append(FormatAge(frame.timestampUnixMilliseconds)).Append(" | ");
                AppendPointerDetails(_textBuilder, frame, pointer);
                AppendFrameLog(_textBuilder.ToString());
            }

            RemoveMissingPointerStates(frame.screen.screenId);
        }

        private void RemoveMissingPointerStates(string screenId)
        {
            string screenPrefix = screenId + ":";
            _stalePointerKeys.Clear();
            foreach (string pointerKey in _lastFramePhases.Keys)
            {
                if (pointerKey.StartsWith(screenPrefix, StringComparison.OrdinalIgnoreCase)
                    && !_seenPointerKeys.Contains(pointerKey))
                {
                    _stalePointerKeys.Add(pointerKey);
                }
            }

            for (int index = 0; index < _stalePointerKeys.Count; index++)
            {
                string pointerKey = _stalePointerKeys[index];
                _lastFramePhases.Remove(pointerKey);
                _nextMoveLogTimes.Remove(pointerKey);
            }
        }

        private static void AppendPointerDetails(
            StringBuilder builder,
            RadarScreenPointerFrame frame,
            RadarScreenPointer pointer)
        {
            builder.Append('[').Append(frame.screen.screenId).Append("/P").Append(pointer.pointerId).Append("] ")
                .Append(pointer.phase)
                .Append(" normalized=(").Append(pointer.normalizedX.ToString("0.000", CultureInfo.InvariantCulture))
                .Append(", ").Append(pointer.normalizedY.ToString("0.000", CultureInfo.InvariantCulture)).Append(')')
                .Append(" pixel=(").Append(pointer.pixelX.ToString("0.0", CultureInfo.InvariantCulture))
                .Append(", ").Append(pointer.pixelY.ToString("0.0", CultureInfo.InvariantCulture)).Append(')')
                .Append(" confidence=").Append(pointer.confidence.ToString("0.00", CultureInfo.InvariantCulture));
        }

        private void AppendFrameLog(string message)
        {
            AppendBounded(
                _frameEntries,
                FrameHistoryLimit,
                $"{DateTime.Now:HH:mm:ss.fff} [FRAME] {message}");
            RenderHistory();
        }

        private void AppendEventLog(string category, string message)
        {
            AppendBounded(
                _eventEntries,
                EventSystemHistoryLimit,
                $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}");
            RenderHistory();
        }

        private static void AppendBounded(Queue<string> entries, int limit, string value)
        {
            entries.Enqueue(value);
            while (entries.Count > Mathf.Max(1, limit))
            {
                entries.Dequeue();
            }
        }

        private void RenderHistory()
        {
            if (eventLogText == null)
            {
                return;
            }

            _textBuilder.Clear();
            _textBuilder.Append("FRAME HISTORY (").Append(_frameEntries.Count).Append(" / ")
                .Append(FrameHistoryLimit).AppendLine(")");
            AppendEntries(_textBuilder, _frameEntries);
            _textBuilder.AppendLine().AppendLine()
                .Append("EVENTSYSTEM HISTORY (").Append(_eventEntries.Count).Append(" / ")
                .Append(EventSystemHistoryLimit).AppendLine(")");
            AppendEntries(_textBuilder, _eventEntries);
            eventLogText.text = _textBuilder.ToString();
            ResizeAndScrollLog();
        }

        private static void AppendEntries(StringBuilder builder, IEnumerable<string> entries)
        {
            foreach (string entry in entries)
            {
                builder.AppendLine(entry);
            }
        }

        private void ResizeAndScrollLog()
        {
            if (eventLogScrollRect == null || eventLogScrollRect.content == null || eventLogScrollRect.viewport == null)
            {
                return;
            }

            float viewportHeight = eventLogScrollRect.viewport.rect.height;
            float contentHeight = Mathf.Max(viewportHeight, eventLogText.preferredHeight + 24f);
            eventLogScrollRect.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);
            eventLogText.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight - 12f);

            _isAutoScrolling = true;
            _ignoreScrollCallbacksThroughFrame = Time.frameCount + 1;
            eventLogScrollRect.verticalNormalizedPosition = 0f;
            _isAutoScrolling = false;
        }

        private static string FormatAge(long unixMilliseconds)
        {
            if (unixMilliseconds <= 0)
            {
                return "n/a";
            }

            long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - unixMilliseconds;
            return age.ToString(CultureInfo.InvariantCulture) + " ms";
        }

        private static string ValueOrUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
        }
    }
}
