using System;
using System.Collections.Generic;
using Blaze.Interaction;
using UnityEngine;

namespace Blaze.Radar
{
    [Obsolete("Use Blaze.Interaction.InteractionPhase.")]
    public enum RadarPointerPhase
    {
        Hover = 0,
        Down = 1,
        Move = 2,
        Up = 3,
        Cancel = 4
    }

    [Obsolete("Use Blaze.Interaction.InteractionInputMode.")]
    public enum RadarInputMode
    {
        RadarOnly = 0,
        RadarAndMouseDebug = 1
    }

    [Serializable, Obsolete("Use Blaze.Interaction.InteractionSurface.")]
    public sealed class RadarScreenInfo
    {
        public string screenId;
        public string name;
        public int widthPixels;
        public int heightPixels;
        public bool isPrimary;
        public int order;
    }

    [Serializable, Obsolete("Use Blaze.Interaction.InteractionPoint.")]
    public sealed class RadarScreenPointer
    {
        public int pointerId;
        public string sourceId;
        public float normalizedX;
        public float normalizedY;
        public float pixelX;
        public float pixelY;
        public float confidence;
        public long timestampUnixMilliseconds;
        public RadarPointerPhase phase;
    }

    [Serializable, Obsolete("Use Blaze.Interaction.InteractionFrame.")]
    public sealed class RadarScreenPointerFrame
    {
        public RadarScreenInfo screen;
        public long sequence;
        public long timestampUnixMilliseconds;
        public List<RadarScreenPointer> pointers = new List<RadarScreenPointer>();
    }

    [Serializable, Obsolete("Use Blaze.Interaction.InteractionFrame.")]
    public sealed class RadarPointerBatchPayload
    {
        public List<RadarScreenPointerFrame> screens = new List<RadarScreenPointerFrame>();
    }

    [Obsolete("Use InteractionBridgeLauncher. This type inherits the single Interaction launcher and owns no process logic.")]
    [AddComponentMenu("Blaze/Radar Compatibility/Bridge Launcher")]
    public sealed class RadarBridgeLauncher : InteractionBridgeLauncher
    {
    }

    [Obsolete("Use InteractionInputModule. This type inherits the single Interaction EventSystem implementation.")]
    [AddComponentMenu("Event/Radar Input Module (Compatibility)")]
    public sealed class RadarInputModule : InteractionInputModule
    {
        public new RadarInputMode InputMode
        {
            get
            {
                return base.InputMode == InteractionInputMode.InteractionAndMouseDebug
                    ? RadarInputMode.RadarAndMouseDebug
                    : RadarInputMode.RadarOnly;
            }
            set
            {
                base.InputMode = value == RadarInputMode.RadarAndMouseDebug
                    ? InteractionInputMode.InteractionAndMouseDebug
                    : InteractionInputMode.InteractionOnly;
            }
        }

        public string ScreenId
        {
            get { return SurfaceId; }
            set { SurfaceId = value; }
        }
    }

    [Obsolete("Use InteractionRuntimeSettings. This wrapper only references provider-neutral settings.")]
    [CreateAssetMenu(fileName = "RadarRuntimeSettingsCompatibility", menuName = "Blaze/Radar Compatibility Settings")]
    public sealed class RadarRuntimeSettings : ScriptableObject
    {
        [SerializeField] private InteractionRuntimeSettings interactionSettings;

        public InteractionRuntimeSettings InteractionSettings
        {
            get { return interactionSettings; }
            set { interactionSettings = value; }
        }

        public bool AutoStart
        {
            get { return interactionSettings == null || interactionSettings.AutoStart; }
        }

        public string PipeName
        {
            get
            {
                return interactionSettings == null
                    ? InteractionIpcProtocol.PipeName
                    : interactionSettings.PipeName;
            }
        }
    }

    [Obsolete("Use InteractionManager and InteractionFrameDispatcher. This adapter owns no IPC client.")]
    [AddComponentMenu("Blaze/Radar Compatibility/Frame Dispatcher")]
    public sealed class RadarFrameDispatcher : MonoBehaviour
    {
        private InteractionManager manager;

        public bool IsConnected { get { return manager != null && manager.IsConnected; } }
        public long DroppedBatchCount { get { return manager == null ? 0L : manager.DroppedFrameCount; } }
        public InteractionManager InteractionManager
        {
            get { return manager; }
            set
            {
                if (ReferenceEquals(manager, value))
                {
                    return;
                }

                Unsubscribe();
                manager = value;
                Subscribe();
            }
        }

        public event Action<bool> ConnectionChanged;
        public event Action<string> ErrorReceived;
        public event Action<RadarScreenPointerFrame> ScreenFrameReceived;
        public event Action<RadarScreenInfo, RadarScreenPointer> ScreenPointerReceived;

        private void OnEnable()
        {
            if (manager == null)
            {
                manager = Blaze.Interaction.InteractionManager.Instance;
            }

            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        internal void ApplyInteractionFrameForTests(InteractionFrame frame, InteractionSurface surface)
        {
            PublishFrame(frame, surface);
        }

        private void Subscribe()
        {
            if (manager == null)
            {
                return;
            }

            manager.FrameReceived -= OnFrame;
            manager.ConnectionChanged -= OnConnectionChanged;
            manager.ErrorReceived -= OnError;
            manager.FrameReceived += OnFrame;
            manager.ConnectionChanged += OnConnectionChanged;
            manager.ErrorReceived += OnError;
        }

        private void Unsubscribe()
        {
            if (manager == null)
            {
                return;
            }

            manager.FrameReceived -= OnFrame;
            manager.ConnectionChanged -= OnConnectionChanged;
            manager.ErrorReceived -= OnError;
        }

        private void OnFrame(InteractionFrame frame)
        {
            if (frame == null)
            {
                return;
            }

            InteractionSurface surface = null;
            var surfaces = manager == null ? null : manager.Surfaces;
            if (surfaces != null)
            {
                for (var index = 0; index < surfaces.Count; index++)
                {
                    if (surfaces[index] != null &&
                        string.Equals(surfaces[index].SurfaceId, frame.SurfaceId, StringComparison.Ordinal))
                    {
                        surface = surfaces[index];
                        break;
                    }
                }
            }

            PublishFrame(frame, surface);
        }

        private void PublishFrame(InteractionFrame frame, InteractionSurface surface)
        {
            var screen = new RadarScreenInfo
            {
                screenId = frame.SurfaceId,
                name = surface == null ? frame.SurfaceId : surface.Name,
                widthPixels = surface == null ? 0 : surface.LogicalWidth,
                heightPixels = surface == null ? 0 : surface.LogicalHeight,
                isPrimary = surface != null && surface.IsPrimary,
                order = surface == null ? 0 : surface.Order
            };
            var radarFrame = new RadarScreenPointerFrame
            {
                screen = screen,
                sequence = frame.Sequence,
                timestampUnixMilliseconds = frame.TimestampUnixMs
            };

            var points = frame.Points ?? new List<InteractionPoint>();
            for (var index = 0; index < points.Count; index++)
            {
                var point = points[index];
                if (point == null)
                {
                    continue;
                }

                var pointer = new RadarScreenPointer
                {
                    pointerId = unchecked((int)point.Id),
                    sourceId = point.SourceId,
                    normalizedX = point.NormalizedPosition == null ? 0f : point.NormalizedPosition.X,
                    normalizedY = point.NormalizedPosition == null ? 0f : point.NormalizedPosition.Y,
                    pixelX = point.PixelPosition == null ? 0f : point.PixelPosition.X,
                    pixelY = point.PixelPosition == null ? 0f : point.PixelPosition.Y,
                    confidence = point.Confidence,
                    timestampUnixMilliseconds = point.TimestampUnixMs,
                    phase = (RadarPointerPhase)point.Phase
                };
                radarFrame.pointers.Add(pointer);
                InvokeSafely(ScreenPointerReceived, screen, pointer);
            }

            InvokeSafely(ScreenFrameReceived, radarFrame);
        }

        private void OnConnectionChanged(bool connected)
        {
            InvokeSafely(ConnectionChanged, connected);
        }

        private void OnError(string error)
        {
            InvokeSafely(ErrorReceived, error);
        }

        private static void InvokeSafely<T>(Action<T> handlers, T value)
        {
            if (handlers == null) return;
            foreach (Action<T> handler in handlers.GetInvocationList())
            {
                try { handler(value); } catch { }
            }
        }

        private static void InvokeSafely<TFirst, TSecond>(
            Action<TFirst, TSecond> handlers,
            TFirst first,
            TSecond second)
        {
            if (handlers == null) return;
            foreach (Action<TFirst, TSecond> handler in handlers.GetInvocationList())
            {
                try { handler(first, second); } catch { }
            }
        }
    }
}
