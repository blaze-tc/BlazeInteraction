using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace Blaze.Interaction
{
    public enum InteractionInputMode
    {
        InteractionOnly = 0,
        InteractionAndMouseDebug = 1
    }

    [CreateAssetMenu(fileName = "InteractionRuntimeSettings", menuName = "Blaze/Interaction Runtime Settings")]
    public sealed class InteractionRuntimeSettings : ScriptableObject
    {
        public const string ResourcesName = "InteractionRuntimeSettings";
        private const int CurrentSurfaceTopologySchemaVersion = 1;

        [Header("Bridge")]
        [SerializeField] private bool autoStart = true;
        [SerializeField] private bool exitBridgeWithUnity = true;
        [SerializeField] private string pipeName = InteractionIpcProtocol.PipeName;
        [SerializeField] private string editorBridgeExecutable = "";
        [SerializeField] private string profilePath = "";

        [Header("Connection")]
        [SerializeField, Min(50)] private int connectTimeoutMilliseconds = 500;
        [SerializeField, Min(25)] private int reconnectDelayMilliseconds = 250;
        [SerializeField, Min(250)] private int serverResponseTimeoutMilliseconds = 4000;

        [Header("Input")]
        [SerializeField] private InteractionInputMode inputMode = InteractionInputMode.InteractionOnly;
        [SerializeField] private bool showDebugOverlay = true;

        [Header("Surfaces")]
        [SerializeField] private List<InteractionSurfaceDefinition> surfaces =
            new List<InteractionSurfaceDefinition> { new InteractionSurfaceDefinition() };
        [SerializeField, HideInInspector] private int surfaceTopologySchemaVersion;

        [NonSerialized] private List<InteractionSurfaceDefinition> readOnlySurfacesSource;
        [NonSerialized] private ReadOnlyCollection<InteractionSurfaceDefinition> readOnlySurfaces;

        public bool AutoStart { get { return autoStart; } }
        public bool ExitBridgeWithUnity { get { return exitBridgeWithUnity; } }
        public string PipeName
        {
            get
            {
                return string.IsNullOrWhiteSpace(pipeName) ? InteractionIpcProtocol.PipeName : pipeName;
            }
        }

        public string EditorBridgeExecutable { get { return editorBridgeExecutable; } }
        public string ProfilePath { get { return profilePath; } }
        public int ConnectTimeoutMilliseconds { get { return Mathf.Max(50, connectTimeoutMilliseconds); } }
        public int ReconnectDelayMilliseconds { get { return Mathf.Max(25, reconnectDelayMilliseconds); } }
        public int ServerResponseTimeoutMilliseconds
        {
            get { return Mathf.Max(250, serverResponseTimeoutMilliseconds); }
        }

        public InteractionInputMode InputMode { get { return inputMode; } }
        public bool ShowDebugOverlay { get { return showDebugOverlay; } }
        public IReadOnlyList<InteractionSurfaceDefinition> Surfaces
        {
            get
            {
                MigrateLegacySurfaceTopology();
                RefreshReadOnlySurfaces();
                return readOnlySurfaces;
            }
        }

        public InteractionSurfaceDefinition PrimarySurface
        {
            get
            {
                MigrateLegacySurfaceTopology();
                for (var index = 0; index < surfaces.Count; index++)
                {
                    var surface = surfaces[index];
                    if (surface != null && surface.Enabled && surface.IsPrimary)
                    {
                        return surface;
                    }
                }

                return null;
            }
        }

        public static InteractionRuntimeSettings LoadOrCreateRuntimeDefaults()
        {
            var settings = Resources.Load<InteractionRuntimeSettings>(ResourcesName);
            return settings != null ? settings : CreateInstance<InteractionRuntimeSettings>();
        }

        internal bool MigrateLegacySurfaceTopology()
        {
            if (surfaceTopologySchemaVersion >= CurrentSurfaceTopologySchemaVersion)
            {
                if (surfaces == null)
                {
                    surfaces = new List<InteractionSurfaceDefinition>();
                    InvalidateReadOnlySurfaces();
                }

                return false;
            }

            if (surfaces == null || surfaces.Count == 0)
            {
                surfaces = new List<InteractionSurfaceDefinition> { new InteractionSurfaceDefinition() };
            }

            surfaceTopologySchemaVersion = CurrentSurfaceTopologySchemaVersion;
            InvalidateReadOnlySurfaces();
            return true;
        }

        private void RefreshReadOnlySurfaces()
        {
            if (!ReferenceEquals(readOnlySurfacesSource, surfaces) || readOnlySurfaces == null)
            {
                readOnlySurfacesSource = surfaces;
                readOnlySurfaces = surfaces.AsReadOnly();
            }
        }

        private void InvalidateReadOnlySurfaces()
        {
            readOnlySurfacesSource = null;
            readOnlySurfaces = null;
        }
    }
}
