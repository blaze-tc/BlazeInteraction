using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Blaze.Radar
{
    [DefaultExecutionOrder(-1000)]
    public sealed class RadarFrameDispatcher : MonoBehaviour
    {
        [SerializeField] private RadarRuntimeSettings settings;
        [SerializeField] private bool autoConnect = true;

        private readonly Dictionary<string, RadarScreenPointerFrame> latestScreenFrames =
            new Dictionary<string, RadarScreenPointerFrame>(StringComparer.OrdinalIgnoreCase);
        private ReadOnlyDictionary<string, RadarScreenPointerFrame> readOnlyLatestScreenFrames;
        private IRadarPipeClient client;
        private long receivedBatchCount;
        private int lastLoggedPointerCount = -1;
        private bool destroying;

        public RadarPointerFrameMessage LatestFrame { get; private set; }
        public IReadOnlyDictionary<string, RadarScreenPointerFrame> LatestScreenFrames
        {
            get { return readOnlyLatestScreenFrames; }
        }

        public RadarPipeClient Client { get { return client as RadarPipeClient; } }
        public bool IsConnected { get { return client != null && client.IsConnected; } }

        public event Action<RadarScreenInfo, RadarScreenPointer> ScreenPointerReceived;
        public event Action<RadarScreenPointerFrame> ScreenFrameReceived;
        public event Action<RadarPointerFrameMessage> PointerFrameReceived;
        public event Action<bool> ConnectionChanged;
        public event Action<string> ErrorReceived;

        private void Awake()
        {
            readOnlyLatestScreenFrames = new ReadOnlyDictionary<string, RadarScreenPointerFrame>(latestScreenFrames);
            EnsureInitialized();
        }

        private void Start()
        {
            if (autoConnect)
            {
                Connect();
            }
        }

        public void Connect()
        {
            if (destroying)
            {
                return;
            }

            EnsureInitialized();
            if (settings == null || client == null)
            {
                ReportError("Radar settings or IPC client could not be initialized.");
                return;
            }

            var validation = RadarScreenTopologyValidator.Validate(settings.Screens);
            if (!validation.IsValid)
            {
                for (var index = 0; index < validation.Errors.Count; index++)
                {
                    ReportError("Invalid screen topology: " + validation.Errors[index]);
                }

                return;
            }

            var enabledScreens = new List<RadarScreenDefinition>();
            for (var index = 0; index < settings.Screens.Count; index++)
            {
                var screen = settings.Screens[index];
                if (screen != null && screen.Enabled)
                {
                    enabledScreens.Add(screen);
                }
            }

            enabledScreens.Sort(CompareScreens);
            var definitions = new List<RadarScreenDefinitionPayload>(enabledScreens.Count);
            for (var index = 0; index < enabledScreens.Count; index++)
            {
                var screen = enabledScreens[index];
                definitions.Add(new RadarScreenDefinitionPayload
                {
                    screenId = screen.ScreenId,
                    name = screen.DisplayName,
                    defaultWidthPixels = screen.DefaultWidthPixels,
                    defaultHeightPixels = screen.DefaultHeightPixels,
                    isPrimary = screen.IsPrimary,
                    order = screen.Order
                });
            }

            client.Start(new RadarHelloPayload
            {
                unityProcessId = Process.GetCurrentProcess().Id,
                unityVersion = Application.unityVersion,
                screens = definitions
            });
        }

        public Task DisconnectAsync()
        {
            if (client == null)
            {
                return Task.CompletedTask;
            }

            return client.StopAsync();
        }

        private void Update()
        {
            UpdateCore();
        }

        private void UpdateCore()
        {
            if (destroying || client == null)
            {
                return;
            }

            client.DrainMainThreadEvents();
            RadarPointerBatchPayload batch;
            if (!client.TryConsumeLatestBatch(out batch))
            {
                return;
            }

            if (batch == null || batch.screens == null)
            {
                ReportError("RadarBridge supplied an empty or malformed PointerBatch.");
                return;
            }

            receivedBatchCount++;
            var pointerCount = 0;
            for (var frameIndex = 0; frameIndex < batch.screens.Count; frameIndex++)
            {
                var frame = batch.screens[frameIndex];
                if (frame == null)
                {
                    ReportError("PointerBatch contains a null screen frame at index " + frameIndex + ".");
                    continue;
                }

                if (frame.screen == null || string.IsNullOrWhiteSpace(frame.screen.screenId))
                {
                    ReportError("PointerBatch contains a screen frame without a screen ID at index " + frameIndex + ".");
                    continue;
                }

                var screenId = frame.screen.screenId;
                latestScreenFrames[screenId] = frame;
                InvokeSafely(ScreenFrameReceived, frame);

                if (frame.pointers == null)
                {
                    ReportError("Screen frame '" + screenId + "' contains a null pointer list.");
                }
                else
                {
                    for (var pointerIndex = 0; pointerIndex < frame.pointers.Count; pointerIndex++)
                    {
                        var pointer = frame.pointers[pointerIndex];
                        if (pointer == null)
                        {
                            ReportError(
                                "Screen frame '" + screenId + "' contains a null pointer at index " + pointerIndex + ".");
                            continue;
                        }

                        pointerCount++;
                        InvokeSafely(ScreenPointerReceived, frame.screen, pointer);
                    }
                }

                var primary = settings != null ? settings.PrimaryScreen : null;
                if (primary != null &&
                    string.Equals(screenId, primary.ScreenId, StringComparison.OrdinalIgnoreCase))
                {
                    var legacy = ToLegacyFrame(frame);
                    LatestFrame = legacy;
                    InvokeSafely(PointerFrameReceived, legacy);
                }
            }

            if (receivedBatchCount == 1 || pointerCount != lastLoggedPointerCount || receivedBatchCount % 120 == 0)
            {
                Debug.Log(
                    "[Blaze Radar] Pointer frame batch screens=" + batch.screens.Count +
                    ", pointers=" + pointerCount +
                    ", received=" + receivedBatchCount +
                    ", dropped=" + client.DroppedBatchCount + ".",
                    this);
                lastLoggedPointerCount = pointerCount;
            }
        }

        private void EnsureInitialized()
        {
            if (settings == null)
            {
                settings = RadarRuntimeSettings.LoadOrCreateRuntimeDefaults();
            }

            if (client == null && settings != null)
            {
                AssignClient(new RadarPipeClient(
                    settings.PipeName,
                    settings.ConnectTimeoutMilliseconds,
                    settings.ReconnectDelayMilliseconds));
            }

            if (readOnlyLatestScreenFrames == null)
            {
                readOnlyLatestScreenFrames = new ReadOnlyDictionary<string, RadarScreenPointerFrame>(latestScreenFrames);
            }
        }

        private void AssignClient(IRadarPipeClient value)
        {
            client = value;
            if (client != null)
            {
                client.ConnectionChanged += OnClientConnectionChanged;
                client.ErrorReceived += OnClientErrorReceived;
            }
        }

        private void UnsubscribeClient(IRadarPipeClient value)
        {
            if (value == null)
            {
                return;
            }

            value.ConnectionChanged -= OnClientConnectionChanged;
            value.ErrorReceived -= OnClientErrorReceived;
        }

        private void OnClientConnectionChanged(bool connected)
        {
            Debug.Log("[Blaze Radar] IPC " + (connected ? "connected" : "disconnected") + ".", this);
            InvokeSafely(ConnectionChanged, connected);
        }

        private void OnClientErrorReceived(string message)
        {
            ReportError(message);
        }

        private async void OnDestroy()
        {
            if (destroying)
            {
                return;
            }

            destroying = true;
            var current = client;
            client = null;
            UnsubscribeClient(current);
            if (current == null)
            {
                return;
            }

            try
            {
                await current.StopAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally
            {
                try
                {
                    current.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
        }

        private void ReportError(string message)
        {
            var value = string.IsNullOrWhiteSpace(message) ? "Unknown Radar IPC error." : message;
            Debug.LogError("[Blaze Radar] IPC error: " + value, this);
            InvokeSafely(ErrorReceived, value);
        }

        private static int CompareScreens(RadarScreenDefinition left, RadarScreenDefinition right)
        {
            var order = left.Order.CompareTo(right.Order);
            return order != 0
                ? order
                : StringComparer.Ordinal.Compare(left.ScreenId ?? string.Empty, right.ScreenId ?? string.Empty);
        }

        private static RadarPointerFrameMessage ToLegacyFrame(RadarScreenPointerFrame frame)
        {
            var legacy = new RadarPointerFrameMessage
            {
                sequence = frame.sequence,
                timestampUnixMilliseconds = frame.timestampUnixMilliseconds,
                pointers = new List<RadarPointerMessage>()
            };
            if (frame.pointers == null)
            {
                return legacy;
            }

            for (var index = 0; index < frame.pointers.Count; index++)
            {
                var pointer = frame.pointers[index];
                if (pointer == null)
                {
                    continue;
                }

                legacy.pointers.Add(new RadarPointerMessage
                {
                    pointerId = pointer.pointerId,
                    phase = pointer.phase,
                    normalizedX = pointer.normalizedX,
                    normalizedY = pointer.normalizedY,
                    confidence = pointer.confidence,
                    timestampUnixMilliseconds = pointer.timestampUnixMilliseconds
                });
            }

            return legacy;
        }

        private void InvokeSafely<T>(Action<T> handlers, T value)
        {
            if (handlers == null)
            {
                return;
            }

            foreach (Action<T> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(value);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
        }

        private void InvokeSafely<TFirst, TSecond>(
            Action<TFirst, TSecond> handlers,
            TFirst first,
            TSecond second)
        {
            if (handlers == null)
            {
                return;
            }

            foreach (Action<TFirst, TSecond> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(first, second);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
        }

        internal void ConfigureForTests(
            RadarRuntimeSettings testSettings,
            IRadarPipeClient testClient,
            bool connectAutomatically)
        {
            if (testClient == null)
            {
                throw new ArgumentNullException(nameof(testClient));
            }

            var previous = client;
            UnsubscribeClient(previous);
            if (previous != null)
            {
                previous.Dispose();
            }

            settings = testSettings;
            autoConnect = connectAutomatically;
            destroying = false;
            AssignClient(testClient);
        }

        internal void TickForTests()
        {
            UpdateCore();
        }
    }
}
