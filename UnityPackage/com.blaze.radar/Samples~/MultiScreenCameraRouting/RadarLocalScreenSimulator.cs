using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blaze.Radar.Samples
{
    /// <summary>
    /// Generates a repeatable three-screen workload without requiring RadarBridge.
    /// Pointer identity is deliberately scoped to each logical screen.
    /// </summary>
    public sealed class RadarLocalScreenSimulator : MonoBehaviour
    {
        private const int CycleFrames = 210;
        private const int SideUpFrame = 180;

        [SerializeField, Min(1f)] private float framesPerSecond = 30f;

        private readonly Dictionary<PointerKey, PointerSnapshot> activePointers =
            new Dictionary<PointerKey, PointerSnapshot>();
        private float elapsedTime;
        private float accumulator;
        private long sequence;
        private bool running;
        private bool shutdownEmitted;

        public event Action<RadarPointerBatchPayload> BatchGenerated;

        public bool IsRunning => running;
        public float FramesPerSecond => Mathf.Max(1f, framesPerSecond);

        private void Update()
        {
            if (!running)
            {
                return;
            }

            var interval = 1f / FramesPerSecond;
            accumulator += Time.unscaledDeltaTime;
            while (accumulator >= interval)
            {
                accumulator -= interval;
                elapsedTime += interval;
                EmitBatch();
            }
        }

        private void OnDisable()
        {
            StopSimulation(true);
        }

        public void StartSimulation()
        {
            if (running)
            {
                return;
            }

            activePointers.Clear();
            elapsedTime = 0f;
            accumulator = 0f;
            sequence = 0L;
            shutdownEmitted = false;
            running = true;
            EmitBatch();
        }

        public void StopSimulation(bool emitPointerUps = true)
        {
            if (!running && activePointers.Count == 0)
            {
                return;
            }

            running = false;
            accumulator = 0f;
            if (!emitPointerUps)
            {
                activePointers.Clear();
                shutdownEmitted = true;
                return;
            }

            var shutdown = BuildShutdownBatch(++sequence);
            if (CountPointers(shutdown) > 0)
            {
                BatchGenerated?.Invoke(shutdown);
            }
        }

        internal RadarPointerBatchPayload BuildBatch(float time, long batchSequence)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var cycleFrame = PositiveModulo(batchSequence - 1L, CycleFrames);
            var leftPointers = BuildSidePointer("left", time, cycleFrame, false, timestamp);
            var frontPointers = BuildFrontPointers(time, batchSequence, timestamp);
            var rightPointers = BuildSidePointer("right", time + 1.2f, cycleFrame, true, timestamp);
            var batch = new RadarPointerBatchPayload
            {
                screens = new List<RadarScreenPointerFrame>
                {
                    MakeFrame("left", "LEFT", 1920, 1440, false, 0, batchSequence, timestamp, leftPointers),
                    MakeFrame("front", "FRONT", 4096, 1536, true, 1, batchSequence, timestamp, frontPointers),
                    MakeFrame("right", "RIGHT", 1920, 1440, false, 2, batchSequence, timestamp, rightPointers)
                }
            };

            TrackLifecycle(batch);
            return batch;
        }

        internal RadarPointerBatchPayload BuildShutdownBatch(long batchSequence)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var frames = EmptyFrames(batchSequence, timestamp);
            if (shutdownEmitted || activePointers.Count == 0)
            {
                shutdownEmitted = true;
                return new RadarPointerBatchPayload { screens = frames };
            }

            foreach (var pair in activePointers)
            {
                var snapshot = pair.Value;
                var pointer = CreatePointer(
                    pair.Key.PointerId,
                    RadarPointerPhase.Up,
                    snapshot.Normalized,
                    snapshot.Width,
                    snapshot.Height,
                    timestamp);
                FindFrame(frames, pair.Key.ScreenId).pointers.Add(pointer);
            }

            activePointers.Clear();
            shutdownEmitted = true;
            return new RadarPointerBatchPayload { screens = frames };
        }

        private void EmitBatch()
        {
            BatchGenerated?.Invoke(BuildBatch(elapsedTime, ++sequence));
        }

        private List<RadarScreenPointer> BuildSidePointer(
            string screenId,
            float time,
            int cycleFrame,
            bool reverse,
            long timestamp)
        {
            var pointers = new List<RadarScreenPointer>(1);
            if (cycleFrame > SideUpFrame)
            {
                return pointers;
            }

            var position = Ellipse(time, reverse ? -1f : 1f, 0.31f, 0.26f);
            var phase = cycleFrame == 0
                ? RadarPointerPhase.Down
                : cycleFrame == SideUpFrame
                    ? RadarPointerPhase.Up
                    : RadarPointerPhase.Move;
            pointers.Add(CreatePointer(1, phase, position, 1920, 1440, timestamp));
            return pointers;
        }

        private static List<RadarScreenPointer> BuildFrontPointers(float time, long batchSequence, long timestamp)
        {
            var travel = 0.5f + 0.35f * Mathf.Sin(time * 0.72f);
            var wave = 0.5f + 0.18f * Mathf.Sin(time * 1.08f);
            var phase = batchSequence <= 1L ? RadarPointerPhase.Down : RadarPointerPhase.Move;
            return new List<RadarScreenPointer>
            {
                CreatePointer(1, phase, new Vector2(travel, wave), 4096, 1536, timestamp),
                CreatePointer(2, phase, new Vector2(1f - travel, 1f - wave), 4096, 1536, timestamp)
            };
        }

        private static Vector2 Ellipse(float time, float direction, float radiusX, float radiusY)
        {
            var angle = time * direction * 1.15f;
            return new Vector2(
                0.5f + Mathf.Cos(angle) * radiusX,
                0.5f + Mathf.Sin(angle) * radiusY);
        }

        private static RadarScreenPointer CreatePointer(
            int id,
            RadarPointerPhase phase,
            Vector2 normalized,
            int width,
            int height,
            long timestamp)
        {
            normalized.x = Mathf.Clamp01(normalized.x);
            normalized.y = Mathf.Clamp01(normalized.y);
            return new RadarScreenPointer
            {
                pointerId = id,
                phase = phase,
                normalizedX = normalized.x,
                normalizedY = normalized.y,
                pixelX = normalized.x * width,
                pixelY = normalized.y * height,
                confidence = 1f,
                timestampUnixMilliseconds = timestamp
            };
        }

        private static RadarScreenPointerFrame MakeFrame(
            string id,
            string name,
            int width,
            int height,
            bool primary,
            int order,
            long batchSequence,
            long timestamp,
            List<RadarScreenPointer> pointers)
        {
            return new RadarScreenPointerFrame
            {
                screen = new RadarScreenInfo
                {
                    screenId = id,
                    name = name,
                    widthPixels = width,
                    heightPixels = height,
                    isPrimary = primary,
                    order = order
                },
                sequence = batchSequence,
                timestampUnixMilliseconds = timestamp,
                pointers = pointers
            };
        }

        private static List<RadarScreenPointerFrame> EmptyFrames(long batchSequence, long timestamp)
        {
            return new List<RadarScreenPointerFrame>
            {
                MakeFrame("left", "LEFT", 1920, 1440, false, 0, batchSequence, timestamp, new List<RadarScreenPointer>()),
                MakeFrame("front", "FRONT", 4096, 1536, true, 1, batchSequence, timestamp, new List<RadarScreenPointer>()),
                MakeFrame("right", "RIGHT", 1920, 1440, false, 2, batchSequence, timestamp, new List<RadarScreenPointer>())
            };
        }

        private void TrackLifecycle(RadarPointerBatchPayload batch)
        {
            for (var frameIndex = 0; frameIndex < batch.screens.Count; frameIndex++)
            {
                var frame = batch.screens[frameIndex];
                for (var pointerIndex = 0; pointerIndex < frame.pointers.Count; pointerIndex++)
                {
                    var pointer = frame.pointers[pointerIndex];
                    var key = new PointerKey(frame.screen.screenId, pointer.pointerId);
                    if (pointer.phase == RadarPointerPhase.Up)
                    {
                        activePointers.Remove(key);
                    }
                    else
                    {
                        activePointers[key] = new PointerSnapshot(
                            new Vector2(pointer.normalizedX, pointer.normalizedY),
                            frame.screen.widthPixels,
                            frame.screen.heightPixels);
                        shutdownEmitted = false;
                    }
                }
            }
        }

        private static RadarScreenPointerFrame FindFrame(List<RadarScreenPointerFrame> frames, string screenId)
        {
            for (var index = 0; index < frames.Count; index++)
            {
                if (string.Equals(frames[index].screen.screenId, screenId, StringComparison.OrdinalIgnoreCase))
                {
                    return frames[index];
                }
            }

            throw new InvalidOperationException("Missing simulator screen frame '" + screenId + "'.");
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

        private static int PositiveModulo(long value, int modulus)
        {
            var result = (int)(value % modulus);
            return result < 0 ? result + modulus : result;
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

        private readonly struct PointerSnapshot
        {
            public PointerSnapshot(Vector2 normalized, int width, int height)
            {
                Normalized = normalized;
                Width = width;
                Height = height;
            }

            public Vector2 Normalized { get; }
            public int Width { get; }
            public int Height { get; }
        }
    }
}
