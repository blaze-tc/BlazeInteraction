using System.Collections.Generic;
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
                EnsureScreenDefaults();
                return screens;
            }
        }

        public RadarScreenDefinition PrimaryScreen
        {
            get
            {
                EnsureScreenDefaults();
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

        private void EnsureScreenDefaults()
        {
            if (screens == null || screens.Count == 0)
            {
                screens = new List<RadarScreenDefinition> { new RadarScreenDefinition() };
            }
        }
    }
}
