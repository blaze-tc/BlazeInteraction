using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace Blaze.Radar
{
    public enum RadarInputMode
    {
        RadarOnly = 0,
        RadarAndMouseDebug = 1
    }

    [CreateAssetMenu(fileName = "RadarRuntimeSettings", menuName = "Blaze/Radar Runtime Settings")]
    public sealed class RadarRuntimeSettings : ScriptableObject
    {
        public const string ResourcesName = "RadarRuntimeSettings";
        private const int CurrentScreenTopologySchemaVersion = 1;

        [Header("Bridge")]
        [SerializeField] private bool autoStart = true;
        [SerializeField] private bool exitBridgeWithUnity = true;
        [SerializeField] private string pipeName = "Yuexin.RadarBridge";
        [SerializeField] private string editorBridgeExecutable = "";
        [SerializeField] private string profilePath = "";

        [Header("Connection")]
        [SerializeField, Min(50)] private int connectTimeoutMilliseconds = 500;
        [SerializeField, Min(50)] private int reconnectDelayMilliseconds = 500;

        [Header("Input")]
        [SerializeField] private RadarInputMode inputMode = RadarInputMode.RadarOnly;
        [SerializeField] private bool showDebugOverlay = true;

        [Header("Screens")]
        [SerializeField] private List<RadarScreenDefinition> screens =
            new List<RadarScreenDefinition> { new RadarScreenDefinition() };
        [SerializeField, HideInInspector] private int screenTopologySchemaVersion;

        [NonSerialized] private List<RadarScreenDefinition> readOnlyScreensSource;
        [NonSerialized] private ReadOnlyCollection<RadarScreenDefinition> readOnlyScreens;

        public bool AutoStart => autoStart;
        public bool ExitBridgeWithUnity => exitBridgeWithUnity;
        public string PipeName => string.IsNullOrWhiteSpace(pipeName) ? "Yuexin.RadarBridge" : pipeName;
        public string EditorBridgeExecutable => editorBridgeExecutable;
        public string ProfilePath => profilePath;
        public int ConnectTimeoutMilliseconds => Mathf.Max(50, connectTimeoutMilliseconds);
        public int ReconnectDelayMilliseconds => Mathf.Max(50, reconnectDelayMilliseconds);
        public RadarInputMode InputMode => inputMode;
        public bool ShowDebugOverlay => showDebugOverlay;
        public IReadOnlyList<RadarScreenDefinition> Screens
        {
            get
            {
                MigrateLegacyScreenTopology();
                RefreshReadOnlyScreens();
                return readOnlyScreens;
            }
        }

        public RadarScreenDefinition PrimaryScreen
        {
            get
            {
                MigrateLegacyScreenTopology();
                for (var index = 0; index < screens.Count; index++)
                {
                    var screen = screens[index];
                    if (screen != null && screen.Enabled && screen.IsPrimary)
                    {
                        return screen;
                    }
                }

                return null;
            }
        }

        public static RadarRuntimeSettings LoadOrCreateRuntimeDefaults()
        {
            var settings = Resources.Load<RadarRuntimeSettings>(ResourcesName);
            return settings != null ? settings : CreateInstance<RadarRuntimeSettings>();
        }

        internal bool MigrateLegacyScreenTopology()
        {
            if (screenTopologySchemaVersion >= CurrentScreenTopologySchemaVersion)
            {
                if (screens == null)
                {
                    screens = new List<RadarScreenDefinition>();
                    InvalidateReadOnlyScreens();
                }

                return false;
            }

            if (screens == null || screens.Count == 0)
            {
                screens = new List<RadarScreenDefinition> { new RadarScreenDefinition() };
            }

            screenTopologySchemaVersion = CurrentScreenTopologySchemaVersion;
            InvalidateReadOnlyScreens();
            return true;
        }

        private void RefreshReadOnlyScreens()
        {
            if (!ReferenceEquals(readOnlyScreensSource, screens) || readOnlyScreens == null)
            {
                readOnlyScreensSource = screens;
                readOnlyScreens = screens.AsReadOnly();
            }
        }

        private void InvalidateReadOnlyScreens()
        {
            readOnlyScreensSource = null;
            readOnlyScreens = null;
        }
    }
}
