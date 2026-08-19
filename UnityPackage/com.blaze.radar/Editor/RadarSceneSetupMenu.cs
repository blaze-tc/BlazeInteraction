using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Blaze.Radar.Editor
{
    public static class RadarSceneSetupMenu
    {
        [MenuItem("GameObject/Blaze Radar/Create Runtime", false, 10)]
        public static void CreateRuntime()
        {
            var root = new GameObject("Blaze Radar Runtime");
            Undo.RegisterCreatedObjectUndo(root, "Create Blaze Radar Runtime");
            var launcher = root.AddComponent<RadarBridgeLauncher>();
            var dispatcher = root.AddComponent<RadarFrameDispatcher>();
            root.AddComponent<RadarDebugOverlay>();

            var eventSystem = Object.FindObjectOfType<EventSystem>();
            if (eventSystem == null)
            {
                var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
                Undo.RegisterCreatedObjectUndo(eventSystemObject, "Create EventSystem");
                eventSystem = eventSystemObject.GetComponent<EventSystem>();
            }

            var standalone = eventSystem.GetComponent<StandaloneInputModule>();
            if (standalone != null)
            {
                standalone.enabled = false;
            }

            var inputModule = eventSystem.GetComponent<RadarInputModule>();
            if (inputModule == null)
            {
                inputModule = Undo.AddComponent<RadarInputModule>(eventSystem.gameObject);
            }

            inputModule.Dispatcher = dispatcher;
            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(launcher);
        }
    }
}
