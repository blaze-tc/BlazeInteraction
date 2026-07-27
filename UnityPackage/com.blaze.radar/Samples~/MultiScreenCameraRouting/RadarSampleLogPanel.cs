using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Blaze.Radar.Samples
{
    /// <summary>
    /// Keeps the sample diagnostics readable: bounded history, per-pointer Move throttling,
    /// and an unthrottled live table for each logical screen.
    /// </summary>
    public sealed class RadarSampleLogPanel : MonoBehaviour
    {
        private const int MaximumLines = 300;
        private const int MaximumRenderedLines = 40;
        private const float MoveLogInterval = 0.1f;

        [Header("Header")]
        [SerializeField] private Text connectionStatusText;
        [SerializeField] private Text frameSummaryText;

        [Header("Live Point Overlays")]
        [SerializeField] private Text leftPointsText;
        [SerializeField] private Text frontPointsText;
        [SerializeField] private Text rightPointsText;

        [Header("Bounded Log")]
        [SerializeField] private Text logText;
        [SerializeField] private ScrollRect logScrollRect;

        private readonly Queue<string> lines = new Queue<string>(MaximumLines);
        private readonly Dictionary<PointerKey, float> nextMoveLogTime =
            new Dictionary<PointerKey, float>();
        private readonly Dictionary<PointerKey, string> livePointers =
            new Dictionary<PointerKey, string>();
        private readonly Dictionary<string, ScreenSummary> screens =
            new Dictionary<string, ScreenSummary>(StringComparer.OrdinalIgnoreCase);
        private readonly StringBuilder builder = new StringBuilder(8192);
        private bool layoutDirty;
        private long lastSequence;
        private long lastTimestamp;
        private long lastDroppedBatches;

        public int LineCount => lines.Count;

        private void LateUpdate()
        {
            if (!layoutDirty)
            {
                return;
            }

            layoutDirty = false;
            RenderHistory();
            RenderLiveTables();
        }

        public void SetConnectionStatus(string text)
        {
            if (connectionStatusText != null)
            {
                connectionStatusText.text = text ?? string.Empty;
            }
        }

        public void RecordFrame(RadarScreenPointerFrame frame, long droppedBatches)
        {
            if (frame == null || frame.screen == null)
            {
                return;
            }

            var count = frame.pointers == null ? 0 : frame.pointers.Count;
            screens[frame.screen.screenId] = new ScreenSummary(
                frame.screen.name,
                frame.screen.widthPixels,
                frame.screen.heightPixels,
                count,
                frame.sequence,
                frame.timestampUnixMilliseconds);
            lastSequence = frame.sequence;
            lastTimestamp = frame.timestampUnixMilliseconds;
            lastDroppedBatches = droppedBatches;
            RenderFrameSummary();

            layoutDirty = true;
        }

        public void RecordHit(
            RadarScreenInfo screen,
            RadarScreenPointer pointer,
            Vector2 cameraPixel,
            Ray ray,
            RaycastHit hit)
        {
            var key = new PointerKey(screen.screenId, pointer.pointerId);
            UpdateLivePointer(key, screen, pointer, "HIT " + hit.collider.name, hit.point);
            if (!ShouldAppend(pointer.phase, key))
            {
                return;
            }

            builder.Clear();
            AppendPointerPrefix(builder, screen, pointer);
            AppendCoordinates(builder, pointer, cameraPixel, ray);
            builder.Append(" hit=").Append(hit.collider.name).Append(" world=");
            AppendVector(builder, hit.point, "0.00");
            AppendLine(builder.ToString());
        }

        public void RecordMiss(
            RadarScreenInfo screen,
            RadarScreenPointer pointer,
            Vector2 cameraPixel,
            Ray ray,
            string reason)
        {
            var key = new PointerKey(screen.screenId, pointer.pointerId);
            UpdateLivePointer(key, screen, pointer, "MISS " + reason, null);
            if (!ShouldAppend(pointer.phase, key))
            {
                return;
            }

            builder.Clear();
            AppendPointerPrefix(builder, screen, pointer);
            AppendCoordinates(builder, pointer, cameraPixel, ray);
            builder.Append(" miss=").Append(reason ?? "unknown");
            AppendLine(builder.ToString());
        }

        public void RecordPointerUp(RadarScreenInfo screen, RadarScreenPointer pointer)
        {
            var key = new PointerKey(screen.screenId, pointer.pointerId);
            livePointers.Remove(key);
            nextMoveLogTime.Remove(key);
            if (screens.TryGetValue(screen.screenId, out var summary))
            {
                screens[screen.screenId] = new ScreenSummary(
                    summary.Name,
                    summary.Width,
                    summary.Height,
                    CountLivePointers(screen.screenId),
                    summary.Sequence,
                    summary.Timestamp);
            }

            RenderFrameSummary();
            builder.Clear();
            AppendPointerPrefix(builder, screen, pointer);
            builder.Append(" logical=(")
                .Append(pointer.pixelX.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(pointer.pixelY.ToString("0.0", CultureInfo.InvariantCulture)).Append(") released");
            AppendLine(builder.ToString());
            layoutDirty = true;
        }

        public void RecordLifecycle(string message)
        {
            AppendLine("[SOURCE] " + (message ?? string.Empty));
        }

        public void RecordError(string message)
        {
            AppendLine("[ERROR] " + (message ?? "Unknown IPC error."));
        }

        private bool ShouldAppend(RadarPointerPhase phase, PointerKey key)
        {
            if (phase != RadarPointerPhase.Move)
            {
                nextMoveLogTime.Remove(key);
                return true;
            }

            var now = Time.unscaledTime;
            if (nextMoveLogTime.TryGetValue(key, out var next) && now < next)
            {
                return false;
            }

            nextMoveLogTime[key] = now + MoveLogInterval;
            return true;
        }

        private void UpdateLivePointer(
            PointerKey key,
            RadarScreenInfo screen,
            RadarScreenPointer pointer,
            string result,
            Vector3? world)
        {
            builder.Clear();
            builder.Append("P").Append(pointer.pointerId).Append("  ")
                .Append(pointer.phase.ToString().ToUpperInvariant()).Append("  ")
                .Append(pointer.normalizedX.ToString("0.000", CultureInfo.InvariantCulture)).Append(", ")
                .Append(pointer.normalizedY.ToString("0.000", CultureInfo.InvariantCulture)).Append("  ")
                .Append(pointer.pixelX.ToString("0", CultureInfo.InvariantCulture)).Append(" x ")
                .Append(pointer.pixelY.ToString("0", CultureInfo.InvariantCulture)).Append("  ")
                .Append(result);
            if (world.HasValue)
            {
                builder.Append("  ");
                AppendVector(builder, world.Value, "0.0");
            }

            livePointers[key] = builder.ToString();
            layoutDirty = true;
        }

        private void AppendLine(string value)
        {
            lines.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + value);
            while (lines.Count > MaximumLines)
            {
                lines.Dequeue();
            }

            layoutDirty = true;
        }

        private void RenderHistory()
        {
            if (logText == null)
            {
                return;
            }

            builder.Clear();
            var skipped = Mathf.Max(0, lines.Count - MaximumRenderedLines);
            var index = 0;
            foreach (var line in lines)
            {
                if (index++ >= skipped)
                {
                    builder.AppendLine(line);
                }
            }

            logText.text = builder.ToString();
            if (logScrollRect != null && logScrollRect.content != null)
            {
                var viewportHeight = logScrollRect.viewport != null
                    ? logScrollRect.viewport.rect.height
                    : 300f;
                var contentHeight = Mathf.Max(viewportHeight, logText.preferredHeight + 24f);
                logScrollRect.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);
                logText.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight - 12f);
                logScrollRect.verticalNormalizedPosition = 0f;
            }
        }

        private void RenderLiveTables()
        {
            RenderScreenTable(leftPointsText, "left", "LEFT  /  1920 x 1440");
            RenderScreenTable(frontPointsText, "front", "FRONT  /  4096 x 1536");
            RenderScreenTable(rightPointsText, "right", "RIGHT  /  1920 x 1440");
        }

        private void RenderScreenTable(Text target, string screenId, string heading)
        {
            if (target == null)
            {
                return;
            }

            builder.Clear();
            builder.AppendLine(heading).AppendLine("ID   PHASE   NORMALIZED      LOGICAL        RESULT");
            var found = false;
            foreach (var pair in livePointers)
            {
                if (string.Equals(pair.Key.ScreenId, screenId, StringComparison.OrdinalIgnoreCase))
                {
                    builder.AppendLine(pair.Value);
                    found = true;
                }
            }

            if (!found)
            {
                builder.Append("No active pointers");
            }

            target.text = builder.ToString();
        }

        private void AppendScreenCount(StringBuilder output, string screenId)
        {
            output.Append(screenId.ToUpperInvariant()).Append(' ');
            output.Append(screens.TryGetValue(screenId, out var summary) ? summary.PointerCount : 0);
        }

        private void RenderFrameSummary()
        {
            if (frameSummaryText == null)
            {
                return;
            }

            builder.Clear();
            builder.Append("SEQ ").Append(lastSequence)
                .Append("  |  AGE ").Append(FormatAge(lastTimestamp))
                .Append("  |  DROPPED ").Append(lastDroppedBatches)
                .Append("  |  ");
            AppendScreenCount(builder, "left");
            builder.Append("   ");
            AppendScreenCount(builder, "front");
            builder.Append("   ");
            AppendScreenCount(builder, "right");
            frameSummaryText.text = builder.ToString();
        }

        private int CountLivePointers(string screenId)
        {
            var count = 0;
            foreach (var key in livePointers.Keys)
            {
                if (string.Equals(key.ScreenId, screenId, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }

        private static void AppendPointerPrefix(
            StringBuilder output,
            RadarScreenInfo screen,
            RadarScreenPointer pointer)
        {
            var label = string.IsNullOrWhiteSpace(screen.name) ? screen.screenId : screen.name;
            output.Append('[').Append(label.ToUpperInvariant()).Append("/P").Append(pointer.pointerId).Append("] ")
                .Append(pointer.phase).Append(' ');
        }

        private static void AppendCoordinates(
            StringBuilder output,
            RadarScreenPointer pointer,
            Vector2 cameraPixel,
            Ray ray)
        {
            output.Append("logical=(")
                .Append(pointer.pixelX.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(pointer.pixelY.ToString("0.0", CultureInfo.InvariantCulture)).Append(") normalized=(")
                .Append(pointer.normalizedX.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                .Append(pointer.normalizedY.ToString("0.000", CultureInfo.InvariantCulture)).Append(") camera=(")
                .Append(cameraPixel.x.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(cameraPixel.y.ToString("0.0", CultureInfo.InvariantCulture)).Append(") ray=");
            AppendVector(output, ray.origin, "0.00");
            output.Append("->");
            AppendVector(output, ray.direction, "0.000");
        }

        private static void AppendVector(StringBuilder output, Vector3 value, string format)
        {
            output.Append('(')
                .Append(value.x.ToString(format, CultureInfo.InvariantCulture)).Append(',')
                .Append(value.y.ToString(format, CultureInfo.InvariantCulture)).Append(',')
                .Append(value.z.ToString(format, CultureInfo.InvariantCulture)).Append(')');
        }

        private static string FormatAge(long timestamp)
        {
            if (timestamp <= 0L)
            {
                return "n/a";
            }

            var age = Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp);
            return age.ToString(CultureInfo.InvariantCulture) + " ms";
        }

        private readonly struct PointerKey : IEquatable<PointerKey>
        {
            public PointerKey(string screenId, int pointerId)
            {
                ScreenId = screenId ?? string.Empty;
                PointerId = pointerId;
            }

            public string ScreenId { get; }
            public int PointerId { get; }

            public bool Equals(PointerKey other)
            {
                return PointerId == other.PointerId &&
                    string.Equals(ScreenId, other.ScreenId, StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object value)
            {
                return value is PointerKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (StringComparer.OrdinalIgnoreCase.GetHashCode(ScreenId) * 397) ^ PointerId;
                }
            }
        }

        private readonly struct ScreenSummary
        {
            public ScreenSummary(
                string name,
                int width,
                int height,
                int pointerCount,
                long sequence,
                long timestamp)
            {
                Name = name;
                Width = width;
                Height = height;
                PointerCount = pointerCount;
                Sequence = sequence;
                Timestamp = timestamp;
            }

            public string Name { get; }
            public int Width { get; }
            public int Height { get; }
            public int PointerCount { get; }
            public long Sequence { get; }
            public long Timestamp { get; }
        }
    }
}
