using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Blaze.Radar.Samples
{
    internal sealed class LatestModeTransitionCoordinator
    {
        private int version;
        private bool hasRequestedMode;
        private int requestedMode;
        private Task currentTask = Task.CompletedTask;

        public Task RequestAsync(
            int mode,
            Action beginTransition,
            Func<Task> disconnectAsync,
            Func<bool> canActivate,
            Action<int> activate)
        {
            if (hasRequestedMode && requestedMode == mode)
            {
                return currentTask;
            }

            hasRequestedMode = true;
            requestedMode = mode;
            var requestVersion = ++version;
            beginTransition();
            currentTask = CompleteAsync(requestVersion, mode, disconnectAsync, canActivate, activate);
            return currentTask;
        }

        public void Cancel()
        {
            version++;
            hasRequestedMode = false;
            currentTask = Task.CompletedTask;
        }

        private async Task CompleteAsync(
            int requestVersion,
            int mode,
            Func<Task> disconnectAsync,
            Func<bool> canActivate,
            Action<int> activate)
        {
            await disconnectAsync();
            if (requestVersion != version || !hasRequestedMode || requestedMode != mode || !canActivate())
            {
                return;
            }

            activate(mode);
        }
    }

    /// <summary>
    /// Keeps LocalSimulation and BridgeIpc mutually exclusive while feeding both sources
    /// through the same screen-frame handler.
    /// </summary>
    public sealed class MultiScreenCameraRoutingPresenter : MonoBehaviour
    {
        public enum DataSourceMode
        {
            LocalSimulation = 0,
            BridgeIpc = 1
        }

        [Header("Scene References")]
        [SerializeField] private RadarFrameDispatcher dispatcher;
        [SerializeField] private RadarLocalScreenSimulator simulator;
        [SerializeField] private RadarWorldPointerVisualizer visualizer;
        [SerializeField] private RadarSampleLogPanel logPanel;

        [Header("Startup")]
        [SerializeField] private DataSourceMode startMode = DataSourceMode.LocalSimulation;

        private readonly Dictionary<PointerKey, PointerSnapshot> activePointers =
            new Dictionary<PointerKey, PointerSnapshot>();
        private readonly List<PointerSnapshot> flushPointers = new List<PointerSnapshot>();
        private readonly List<PointerKey> removePointerKeys = new List<PointerKey>();
        private readonly LatestModeTransitionCoordinator modeTransitions =
            new LatestModeTransitionCoordinator();
        private DataSourceMode currentMode;
        private bool hasActiveMode;
        private bool acceptingLocal;
        private bool acceptingIpc;
        private bool subscribed;

        public DataSourceMode CurrentMode => currentMode;

        private void OnEnable()
        {
            Subscribe();
            SwitchMode(startMode);
        }

        private void Update()
        {
            RefreshStatus();
        }

        private void OnDisable()
        {
            modeTransitions.Cancel();
            acceptingLocal = false;
            acceptingIpc = false;
            simulator?.StopSimulation(false);
            FlushActivePointers("presenter disabled");
            Unsubscribe();
            if (dispatcher != null)
            {
                _ = DisconnectIgnoringErrorsAsync();
            }

            hasActiveMode = false;
        }

        public void SwitchToLocalSimulation()
        {
            SwitchMode(DataSourceMode.LocalSimulation);
        }

        public void SwitchToBridgeIpc()
        {
            SwitchMode(DataSourceMode.BridgeIpc);
        }

        public void SwitchMode(DataSourceMode mode)
        {
            _ = modeTransitions.RequestAsync(
                (int)mode,
                () => BeginModeTransition(mode),
                dispatcher != null
                    ? (Func<Task>)DisconnectIgnoringErrorsAsync
                    : () => Task.CompletedTask,
                () => isActiveAndEnabled,
                rawMode => ActivateMode((DataSourceMode)rawMode));
        }

        private void BeginModeTransition(DataSourceMode mode)
        {
            hasActiveMode = false;
            acceptingLocal = false;
            acceptingIpc = false;
            simulator?.StopSimulation(false);
            FlushActivePointers("switching data source");
            logPanel?.RecordLifecycle("Switching exclusively to " + mode + ".");
        }

        private void ActivateMode(DataSourceMode mode)
        {
            currentMode = mode;
            hasActiveMode = true;
            if (mode == DataSourceMode.LocalSimulation)
            {
                acceptingLocal = true;
                simulator?.StartSimulation();
            }
            else
            {
                acceptingIpc = true;
                dispatcher?.Connect();
            }

            RefreshStatus();
        }

        private void Subscribe()
        {
            if (subscribed)
            {
                return;
            }

            if (simulator != null)
            {
                simulator.BatchGenerated += OnLocalBatchGenerated;
            }

            if (dispatcher != null)
            {
                dispatcher.ScreenFrameReceived += OnDispatcherScreenFrame;
                dispatcher.ConnectionChanged += OnConnectionChanged;
                dispatcher.ErrorReceived += OnDispatcherError;
            }

            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed)
            {
                return;
            }

            if (simulator != null)
            {
                simulator.BatchGenerated -= OnLocalBatchGenerated;
            }

            if (dispatcher != null)
            {
                dispatcher.ScreenFrameReceived -= OnDispatcherScreenFrame;
                dispatcher.ConnectionChanged -= OnConnectionChanged;
                dispatcher.ErrorReceived -= OnDispatcherError;
            }

            subscribed = false;
        }

        private void OnLocalBatchGenerated(RadarPointerBatchPayload batch)
        {
            if (!acceptingLocal || batch == null || batch.screens == null)
            {
                return;
            }

            for (var index = 0; index < batch.screens.Count; index++)
            {
                HandleScreenFrame(batch.screens[index], 0L);
            }
        }

        private void OnDispatcherScreenFrame(RadarScreenPointerFrame frame)
        {
            if (!acceptingIpc)
            {
                return;
            }

            var dropped = dispatcher != null && dispatcher.Client != null
                ? dispatcher.Client.DroppedBatchCount
                : 0L;
            HandleScreenFrame(frame, dropped);
        }

        private void HandleScreenFrame(RadarScreenPointerFrame frame, long droppedBatches)
        {
            if (frame == null || frame.screen == null)
            {
                return;
            }

            TrackPointers(frame);
            visualizer?.ProcessFrame(frame, droppedBatches);
        }

        private void TrackPointers(RadarScreenPointerFrame frame)
        {
            if (frame.pointers == null || frame.pointers.Count == 0)
            {
                RemoveTrackedPointers(frame.screen.screenId);
                logPanel?.ClearScreen(frame.screen.screenId);
                return;
            }

            for (var index = 0; index < frame.pointers.Count; index++)
            {
                var pointer = frame.pointers[index];
                if (pointer == null)
                {
                    continue;
                }

                var key = new PointerKey(frame.screen.screenId, pointer.pointerId);
                if (pointer.phase == RadarPointerPhase.Up)
                {
                    activePointers.Remove(key);
                }
                else
                {
                    activePointers[key] = new PointerSnapshot(frame.screen, pointer);
                }
            }
        }

        private void RemoveTrackedPointers(string screenId)
        {
            removePointerKeys.Clear();
            foreach (var key in activePointers.Keys)
            {
                if (string.Equals(key.ScreenId, screenId, StringComparison.OrdinalIgnoreCase))
                {
                    removePointerKeys.Add(key);
                }
            }

            for (var index = 0; index < removePointerKeys.Count; index++)
            {
                activePointers.Remove(removePointerKeys[index]);
            }
        }

        private void FlushActivePointers(string reason)
        {
            if (activePointers.Count == 0)
            {
                visualizer?.ReleaseAll(reason);
                return;
            }

            flushPointers.Clear();
            foreach (var pointer in activePointers.Values)
            {
                flushPointers.Add(pointer);
            }

            activePointers.Clear();
            for (var index = 0; index < flushPointers.Count; index++)
            {
                var snapshot = flushPointers[index];
                var up = new RadarScreenPointer
                {
                    pointerId = snapshot.Pointer.pointerId,
                    phase = RadarPointerPhase.Up,
                    normalizedX = snapshot.Pointer.normalizedX,
                    normalizedY = snapshot.Pointer.normalizedY,
                    pixelX = snapshot.Pointer.pixelX,
                    pixelY = snapshot.Pointer.pixelY,
                    confidence = snapshot.Pointer.confidence,
                    timestampUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                visualizer?.ProcessPointer(snapshot.Screen, up);
            }

            visualizer?.ReleaseAll(reason);
        }

        private void RefreshStatus()
        {
            if (!hasActiveMode)
            {
                logPanel?.SetConnectionStatus("SOURCE TRANSITION  |  recycling active pointers");
                return;
            }

            if (currentMode == DataSourceMode.LocalSimulation)
            {
                logPanel?.SetConnectionStatus(
                    "LOCAL SIMULATION  |  deterministic 30 FPS  |  RadarBridge disconnected");
                return;
            }

            var client = dispatcher != null ? dispatcher.Client : null;
            var state = dispatcher != null && dispatcher.IsConnected ? "CONNECTED" : "CONNECTING";
            var version = client != null && !string.IsNullOrWhiteSpace(client.BridgeVersion)
                ? client.BridgeVersion
                : "pending";
            var protocol = client != null && client.AcknowledgedProtocolVersion > 0
                ? client.AcknowledgedProtocolVersion.ToString()
                : "pending";
            logPanel?.SetConnectionStatus(
                "BRIDGE IPC  |  " + state + "  |  Bridge " + version + "  |  protocol v" + protocol);
        }

        private void OnConnectionChanged(bool connected)
        {
            logPanel?.RecordLifecycle("RadarBridge " + (connected ? "connected" : "disconnected") + ".");
            RefreshStatus();
        }

        private void OnDispatcherError(string message)
        {
            logPanel?.RecordError(message);
        }

        private async Task DisconnectIgnoringErrorsAsync()
        {
            try
            {
                await dispatcher.DisconnectAsync();
            }
            catch (Exception exception)
            {
                logPanel?.RecordError("Disconnect failed: " + exception.Message);
            }
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
            public PointerSnapshot(RadarScreenInfo screen, RadarScreenPointer pointer)
            {
                Screen = screen;
                Pointer = pointer;
            }

            public RadarScreenInfo Screen { get; }
            public RadarScreenPointer Pointer { get; }
        }
    }
}
