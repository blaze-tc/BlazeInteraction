using UnityEngine;

namespace Blaze.Radar
{
    public sealed class RadarDebugOverlay : MonoBehaviour
    {
        [SerializeField] private RadarFrameDispatcher dispatcher;
        [SerializeField] private bool visible = true;

        private GUIStyle _panelStyle;
        private GUIStyle _labelStyle;

        private void Awake()
        {
            if (dispatcher == null)
            {
                dispatcher = FindObjectOfType<RadarFrameDispatcher>();
            }
        }

        private void OnGUI()
        {
            if (!visible || dispatcher == null)
            {
                return;
            }

            EnsureStyles();
            var client = dispatcher.Client;
            GUILayout.BeginArea(new Rect(16f, 16f, 300f, 118f), _panelStyle);
            GUILayout.Label("BLAZE RADAR", _labelStyle);
            GUILayout.Label(client != null && client.IsConnected ? "IPC: CONNECTED" : "IPC: DISCONNECTED", _labelStyle);
            GUILayout.Label($"Bridge: {client?.BridgeVersion ?? "-"}  Model: {client?.DeviceModel ?? "-"}", _labelStyle);
            GUILayout.Label($"Pointers: {dispatcher.LatestFrame?.pointers?.Count ?? 0}  Dropped: {client?.DroppedFrameCount ?? 0}", _labelStyle);
            if (!string.IsNullOrWhiteSpace(client?.LastError))
            {
                GUILayout.Label(client.LastError, _labelStyle);
            }
            GUILayout.EndArea();
        }

        private void EnsureStyles()
        {
            if (_panelStyle != null)
            {
                return;
            }

            _panelStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(12, 12, 10, 10),
                alignment = TextAnchor.UpperLeft
            };
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.75f, 0.95f, 0.97f) },
                fontSize = 12
            };
        }
    }
}
