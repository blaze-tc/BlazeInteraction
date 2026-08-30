using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Blaze.Interaction.Editor
{
    public static class InteractionSceneSetupMenu
    {
        [MenuItem("GameObject/Blaze Interaction/Create Runtime", false, 10)]
        public static void CreateRuntime()
        {
            var root = new GameObject("Blaze Interaction Runtime");
            Undo.RegisterCreatedObjectUndo(root, "Create Blaze Interaction Runtime");
            var launcher = root.AddComponent<InteractionBridgeLauncher>();
            root.AddComponent<InteractionCameraRouter>();

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

            if (eventSystem.GetComponent<InteractionInputModule>() == null)
            {
                Undo.AddComponent<InteractionInputModule>(eventSystem.gameObject);
            }

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(launcher);
        }
    }
}
